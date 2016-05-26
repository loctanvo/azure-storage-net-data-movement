//------------------------------------------------------------------------------
// <copyright file="StreamedWriter.cs" company="Microsoft">
//    Copyright (c) Microsoft Corporation
// </copyright>
//------------------------------------------------------------------------------

namespace Microsoft.WindowsAzure.Storage.DataMovement.TransferControllers
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed class StreamedWriter : TransferReaderWriterBase, IDisposable
    {
        /// <summary>
        /// Streamed destination is written sequentially. 
        /// This variable records offset of next chunk to be written.
        /// </summary>
        private long expectOffset = 0;

        /// <summary>
        /// Value to indicate whether there's work to do in the writer.
        /// </summary>
        private volatile bool hasWork;

        /// <summary>
        /// Stream to calculation destination's content MD5.
        /// </summary>
        private MD5HashStream md5HashStream;

        private Stream outputStream;

        /// <summary>
        /// Value to indicate whether the stream is a file stream opened by the writer or input by user.
        /// If it's a file stream opened by the writer, we should closed it after transferring finished.
        /// </summary>
        private bool ownsStream;

        private volatile State state;

        public StreamedWriter(
            TransferScheduler scheduler,
            SyncTransferController controller,
            CancellationToken cancellationToken)
            : base(scheduler, controller, cancellationToken)
        {
            this.hasWork = true;
            this.state = State.OpenOutputStream;
        }

        private enum State
        {
            OpenOutputStream,
            CalculateMD5,
            Write,
            Error,
            Finished
        };

        private TransferJob TransferJob
        {
            get
            {
                return this.SharedTransferData.TransferJob;
            }
        }

        public override bool HasWork
        {
            get
            {
                return this.hasWork &&
                    ((State.OpenOutputStream == this.state)
                    || (State.CalculateMD5 == this.state)
                    || ((State.Write == this.state)
                        && ((this.SharedTransferData.TotalLength == this.expectOffset) || this.SharedTransferData.AvailableData.ContainsKey(this.expectOffset))));
            }
        }

        public override bool IsFinished
        {
            get
            {
                return State.Error == this.state || State.Finished == this.state;
            }
        }

        public override async Task DoWorkInternalAsync()
        {
            switch (this.state)
            {
                case State.OpenOutputStream:
                    await HandleOutputStreamAsync();
                    break;
                case State.CalculateMD5:
                    await CalculateMD5Async();
                    break;
                case State.Write:
                    await this.WriteChunkDataAsync();
                    break;
                default:
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                this.CloseOwnedOutputStream();
            }
        }

        private async Task HandleOutputStreamAsync()
        {
            this.hasWork = false;

            await TaskEx.Run(() =>
            {
                if (TransferLocationType.Stream == this.TransferJob.Destination.Type)
                {
                    Stream streamInDestination = (this.TransferJob.Destination as StreamLocation).Stream;
                    if (!streamInDestination.CanWrite)
                    {
                        throw new NotSupportedException(string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.StreamMustSupportWriteException,
                            "outputStream"));
                    }

                    if (!streamInDestination.CanSeek)
                    {
                        throw new NotSupportedException(string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.StreamMustSupportSeekException,
                            "outputStream"));
                    }

                    this.outputStream = streamInDestination;
                }
                else
                {
                    string filePath = (this.TransferJob.Destination as FileLocation).FilePath;
                    this.Controller.CheckOverwrite(
                        File.Exists(filePath),
                        this.SharedTransferData.SourceLocation,
                        filePath);

                    this.Controller.UpdateProgressAddBytesTransferred(0);

                    this.Controller.CheckCancellation();

                    // We do check point consistancy validation in reader, and directly use it in writer.
                    if ((null != this.TransferJob.CheckPoint.TransferWindow)
                        && (this.TransferJob.CheckPoint.TransferWindow.Any()))
                    {
                        this.TransferJob.CheckPoint.TransferWindow.Sort();
                        this.expectOffset = this.TransferJob.CheckPoint.TransferWindow[0];
                    }
                    else
                    {
                        this.expectOffset = this.TransferJob.CheckPoint.EntryTransferOffset;
                    }

                    try
                    {
                        FileMode fileMode = 0 == this.expectOffset ? FileMode.OpenOrCreate : FileMode.Open;

                        // Attempt to open the file first so that we throw an exception before getting into the async work
                        this.outputStream = new FileStream(
                            filePath,
                            fileMode,
                            FileAccess.ReadWrite,
                            FileShare.None);

                        this.ownsStream = true;
                    }
                    catch (Exception ex)
                    {
                        string exceptionMessage = string.Format(
                                    CultureInfo.CurrentCulture,
                                    Resources.FailedToOpenFileException,
                                    filePath,
                                    ex.Message);

                        throw new TransferException(
                                TransferErrorCode.OpenFileFailed,
                                exceptionMessage,
                                ex);
                    }
                }

                this.outputStream.SetLength(this.SharedTransferData.TotalLength);

                this.Controller.UpdateProgressAddBytesTransferred(0);

                this.md5HashStream = new MD5HashStream(
                    this.outputStream,
                    this.expectOffset,
                    !this.SharedTransferData.DisableContentMD5Validation);

                if (this.md5HashStream.FinishedSeparateMd5Calculator)
                {
                    this.state = State.Write;
                }
                else
                {
                    this.state = State.CalculateMD5;
                }

                this.PreProcessed = true;
                this.hasWork = true;
            });
        }

        private Task CalculateMD5Async()
        {
            Debug.Assert(
                this.state == State.CalculateMD5,
                "GetCalculateMD5Action called, but state isn't CalculateMD5",
                "Current state is {0}",
                this.state);

            this.state = State.Write;
            this.hasWork = true;

            return TaskEx.Run(
                delegate
                {
                    this.md5HashStream.CalculateMd5(this.Scheduler.MemoryManager, this.Controller.CheckCancellation);
                });
        }

        private async Task WriteChunkDataAsync()
        {
            Debug.Assert(
                this.state == State.Write || this.state == State.Error,
                "WriteChunkDataAsync called, but state isn't Write or Error",
                "Current state is {0}",
                this.state);

            this.hasWork = false;
            long currentWriteOffset = this.expectOffset;
            TransferData transferData;
            if (this.SharedTransferData.AvailableData.TryRemove(this.expectOffset, out transferData))
            {
                this.expectOffset = Math.Min(this.expectOffset + transferData.Length, this.SharedTransferData.TotalLength);
            }
            else
            {
                this.SetHasWorkOrFinished();
                return;
            }

            Debug.Assert(null != transferData, "TransferData in available data should not be null");
            Debug.Assert(currentWriteOffset == transferData.StartOffset, "StartOffset of TransferData in available data should be the same with the key.");

            try
            {
                await this.md5HashStream.WriteAsync(
                    currentWriteOffset,
                    transferData.MemoryBuffer,
                    0,
                    transferData.Length,
                    this.CancellationToken);

                // If MD5HashTransformBlock returns false, it means some error happened in md5HashStream to calculate MD5.
                // then exception was already thrown out there, don't do anything more here.
                if (!this.md5HashStream.MD5HashTransformBlock(
                    transferData.StartOffset,
                    transferData.MemoryBuffer,
                    0,
                    transferData.Length,
                    null,
                    0))
                {
                    return;
                }
            }
            finally
            {
                this.Scheduler.MemoryManager.ReleaseBuffer(transferData.MemoryBuffer);
            }

            int blockSize = this.Scheduler.TransferOptions.BlockSize;
            long chunkStartOffset = (currentWriteOffset / blockSize) * blockSize;

            if ((currentWriteOffset + transferData.Length) >= Math.Min(chunkStartOffset + blockSize, this.SharedTransferData.TotalLength))
            {
                lock (this.TransferJob.CheckPoint.TransferWindowLock)
                {
                    this.TransferJob.CheckPoint.TransferWindow.Remove(chunkStartOffset);
                }
            }

            this.Controller.UpdateProgressAddBytesTransferred(transferData.Length);
            this.SetHasWorkOrFinished();
        }

        private void SetHasWorkOrFinished()
        {
            if (this.expectOffset == this.SharedTransferData.TotalLength)
            {
                Exception ex = null;
                if (this.md5HashStream.CheckMd5Hash && this.md5HashStream.SucceededSeparateMd5Calculator)
                {
                    this.md5HashStream.MD5HashTransformFinalBlock(new byte[0], 0, 0);

                    string calculatedMd5 = Convert.ToBase64String(this.md5HashStream.Hash);
                    string storedMd5 = this.SharedTransferData.Attributes.ContentMD5;

                    if (!calculatedMd5.Equals(storedMd5))
                    {
                        ex = new InvalidOperationException(
                                string.Format(
                                    CultureInfo.CurrentCulture,
                                    Resources.DownloadedMd5MismatchException,
                                    this.SharedTransferData.SourceLocation,
                                    calculatedMd5,
                                    storedMd5));
                    }
                }

                this.CloseOwnedOutputStream();
                this.NotifyFinished(ex);
                this.state = State.Finished;
            }
            else
            {
                this.hasWork = true;
            }
        }

        private void CloseOwnedOutputStream()
        {
            if (null != this.md5HashStream)
            {
                this.md5HashStream.Dispose();
                this.md5HashStream = null;
            }

            if (this.ownsStream)
            {
                if (null != this.outputStream)
                {
                    this.outputStream.Close();
                    this.outputStream = null;
                }
            }
        }
    }
}
