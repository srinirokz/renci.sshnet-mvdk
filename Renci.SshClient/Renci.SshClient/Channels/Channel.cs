﻿using System;
using System.Threading;
using Renci.SshClient.Common;
using Renci.SshClient.Messages;
using Renci.SshClient.Messages.Connection;

namespace Renci.SshClient.Channels
{
    internal abstract class Channel : IDisposable
    {
        private EventWaitHandle _channelOpenWaitHandle = new AutoResetEvent(false);

        private EventWaitHandle _channelClosedWaitHandle = new AutoResetEvent(false);

        private uint _initialWindowSize = 0x100000;

        private uint _maximumPacketSize = 0x4000;

        private int _chanelOpenFailedAttempts = 0;

        public abstract ChannelTypes ChannelType { get; }

        public uint ClientChannelNumber { get; set; }

        public uint ServerChannelNumber { get; set; }

        public uint WindowSize { get; set; }

        public uint PacketSize { get; set; }

        public bool IsOpen { get; private set; }

        protected Session Session { get; private set; }

        public Channel(Session session, uint channelId, uint windowSize, uint packetSize)
        {
            this._initialWindowSize = windowSize;
            this._maximumPacketSize = Math.Max(packetSize, 0x8000); //  Ensure minimum maximum packet size of 0x8000 bytes

            this.ClientChannelNumber = channelId;

            this.Session = session;
            this.WindowSize = this._initialWindowSize;  // Initial window size
            this.PacketSize = this._maximumPacketSize;     // Maximum packet size

            this.Session.RegisterMessageType<ChannelOpenConfirmationMessage>(MessageTypes.ChannelOpenConfirmation);
            this.Session.RegisterMessageType<ChannelOpenFailureMessage>(MessageTypes.ChannelOpenFailure);
            this.Session.RegisterMessageType<ChannelWindowAdjustMessage>(MessageTypes.ChannelWindowAdjust);
            this.Session.RegisterMessageType<ChannelExtendedDataMessage>(MessageTypes.ChannelExtendedData);
            this.Session.RegisterMessageType<ChannelRequestMessage>(MessageTypes.ChannelRequest);
            this.Session.RegisterMessageType<ChannelSuccessMessage>(MessageTypes.ChannelSuccess);
            this.Session.RegisterMessageType<ChannelDataMessage>(MessageTypes.ChannelData);
            this.Session.RegisterMessageType<ChannelEofMessage>(MessageTypes.ChannelEof);
            this.Session.RegisterMessageType<ChannelCloseMessage>(MessageTypes.ChannelClose);
        }

        public Channel(Session session, uint channelId)
            : this(session, channelId, 0x100000, 0x8000)
        {
        }

        public virtual void Open()
        {
            this.Session.MessageReceived += SessionInfo_MessageReceived;

            //  Open session channel
            if (!this.IsOpen)
            {
                this.SendChannelOpenMessage();
                this.Session.WaitHandle(this._channelOpenWaitHandle);
            }
        }

        public virtual void Close()
        {
            if (this.IsOpen)
            {
                this.SendMessage(new ChannelCloseMessage
                {
                    ChannelNumber = this.ServerChannelNumber,
                });

                //  Wait for channel to be closed
                this.Session.WaitHandle(this._channelClosedWaitHandle);
            }

            this.CloseCleanup();
        }

        protected virtual void OnChannelData(string data)
        {
        }

        protected virtual void OnChannelExtendedData(string data, uint dataTypeCode)
        {
        }

        protected virtual void OnChannelSuccess()
        {
        }

        protected virtual void OnChannelEof()
        {
        }

        protected virtual void OnChannelClose()
        {
            //  TODO:   Channel is closed when it sent and received Close message
        }

        protected virtual void OnChannelFailed(uint reasonCode, string description)
        {
        }

        protected void SendMessage(Message message)
        {
            this.Session.SendMessage(message);
        }

        private void SessionInfo_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            ChannelMessage message = e.Message as ChannelMessage;

            //  Handle only messages belong to this channel or channel open confirmation
            if (message.ChannelNumber == this.ClientChannelNumber || e.Message is ChannelOpenConfirmationMessage)
            {
                this.HandleMessage((dynamic)e.Message);
            }
        }

        #region Message handlers

        private void HandleMessage<T>(T message) where T : Message
        {
            throw new NotSupportedException(string.Format("Message type '{0}' is not supported.", message.MessageType));
        }

        private void HandleMessage(ChannelOpenConfirmationMessage message)
        {
            //  Make sure we open channel only for requested channel number
            if (this.ClientChannelNumber != message.ChannelNumber)
                return;

            this.ServerChannelNumber = message.ServerChannelNumber;
            this.IsOpen = true;
            this.WindowSize = message.InitialWindowSize;
            this.PacketSize = message.MaximumPacketSize;
            this._channelOpenWaitHandle.Set();
        }

        private void HandleMessage(ChannelOpenFailureMessage message)
        {
            if (_chanelOpenFailedAttempts > this.Session.ConnectionInfo.RetryAttempts)
            {
                this.IsOpen = false;

                this._channelOpenWaitHandle.Set();
                this.OnChannelFailed(message.ReasonCode, message.Description);
            }
            else
            {
                //  Send channel open message again
                this.SendChannelOpenMessage();
            }
        }

        private void HandleMessage(ChannelWindowAdjustMessage message)
        {
            this.WindowSize += message.BytesToAdd;
        }

        private void HandleMessage(ChannelDataMessage message)
        {
            this.AdjustDataWindow(message.Data);
            this.OnChannelData(message.Data);
        }

        private void HandleMessage(ChannelExtendedDataMessage message)
        {
            this.AdjustDataWindow(message.Data);
            this.OnChannelExtendedData(message.Data, message.DataTypeCode);
        }

        private void HandleMessage(ChannelRequestMessage message)
        {
            Message replyMessage = new ChannelFailureMessage()
            {
                ChannelNumber = message.ChannelNumber,
            };

            if (message.RequestName == RequestNames.ExitStatus)
            {
                var exitStatus = message.ExitStatus;
                replyMessage = new ChannelSuccessMessage()
                {
                    ChannelNumber = message.ChannelNumber,
                };
            }

            if (message.WantReply)
            {
                this.SendMessage(replyMessage);
            }
        }

        private void HandleMessage(ChannelSuccessMessage message)
        {
            this.OnChannelSuccess();
        }

        private void HandleMessage(ChannelEofMessage message)
        {
            this.OnChannelEof();
        }

        private void HandleMessage(ChannelCloseMessage message)
        {
            this.CloseCleanup();

            this._channelClosedWaitHandle.Set();
        }

        private void AdjustDataWindow(string messageData)
        {
            this.WindowSize -= (uint)messageData.Length;

            //  Adjust window if window size is too low
            if (this.WindowSize < this._initialWindowSize / 2)
            {
                this.SendMessage(new ChannelWindowAdjustMessage
                {
                    ChannelNumber = this.ServerChannelNumber,
                    BytesToAdd = this._initialWindowSize - this.WindowSize,
                });
                this.WindowSize = this._initialWindowSize;
            }
        }

        #endregion

        private void SendChannelOpenMessage()
        {
            this.SendMessage(new ChannelOpenMessage
            {
                ChannelName = "session",
                ChannelNumber = this.ClientChannelNumber,
                InitialWindowSize = this.WindowSize,
                MaximumPacketSize = this.PacketSize,
            });
        }

        private void CloseCleanup()
        {
            if (this.IsOpen)
            {
                this.IsOpen = false;

                this.Session.MessageReceived -= SessionInfo_MessageReceived;
            }
        }

        #region IDisposable Members

        protected abstract void OnDisposing();

        private bool disposed = false;

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!this.disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    // Dispose managed resources.
                    if (this._channelOpenWaitHandle != null)
                    {
                        this._channelOpenWaitHandle.Dispose();
                    }
                    if (this._channelClosedWaitHandle != null)
                    {
                        this._channelClosedWaitHandle.Dispose();
                    }

                    this.OnDisposing();
                }

                // Note disposing has been done.
                disposed = true;
            }
        }

        ~Channel()
        {
            // Do not re-create Dispose clean-up code here.
            // Calling Dispose(false) is optimal in terms of
            // readability and maintainability.
            Dispose(false);
        }

        #endregion
    }
}
