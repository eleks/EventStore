using System;
using System.Diagnostics;
using System.Threading;
using EventStore.Common.Log;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.Services.RequestManager.Managers
{
    public class TwoPhaseRequestManagerBase :           IHandle<ReplicationMessage.AlreadyCommitted>,
                                                        IHandle<ReplicationMessage.PrepareAck>,
                                                        IHandle<ReplicationMessage.CommitAck>,
                                                        IHandle<ReplicationMessage.WrongExpectedVersion>,
                                                        IHandle<ReplicationMessage.StreamDeleted>,
                                                        IHandle<ReplicationMessage.PreparePhaseTimeout>,
                                                        IHandle<ReplicationMessage.CommitPhaseTimeout>
    {
         
        protected readonly IPublisher Publisher;
        protected readonly IEnvelope _publishEnvelope;
        protected IEnvelope _responseEnvelope;
        protected Guid _correlationId;
        protected string _eventStreamId;

        protected int _awaitingPrepare;
        protected int _awaitingCommit;

        protected long _preparePos = -1;

        protected bool _completed;
        protected bool _initialized;

        public TwoPhaseRequestManagerBase(IPublisher publisher, int prepareCount, int commitCount)
        {
            if (publisher == null) 
                throw new ArgumentNullException();
            if (prepareCount <= 0 || commitCount <= 0) 
                throw new ArgumentOutOfRangeException("counts for prepare and commit acks must be a positive number");
            Publisher = publisher;
            _awaitingCommit = commitCount;
            _awaitingPrepare = prepareCount;
            _publishEnvelope = new PublishEnvelope(publisher);
        }


        public void Handle(ReplicationMessage.WrongExpectedVersion message)
        {
            if (_completed)
                return;

            CompleteFailedRequest(message.CorrelationId, _eventStreamId, OperationErrorCode.WrongExpectedVersion, "Wrong expected version.");
        }

        public void Handle(ReplicationMessage.StreamDeleted message)
        {
            if (_completed)
                return;

            CompleteFailedRequest(message.CorrelationId, _eventStreamId, OperationErrorCode.StreamDeleted, "Stream is deleted.");
        }

        public void Handle(ReplicationMessage.PreparePhaseTimeout message)
        {
            if (_completed || _awaitingPrepare == 0)
                return;

            CompleteFailedRequest(message.CorrelationId, _eventStreamId, OperationErrorCode.PrepareTimeout, "Prepare phase timeout.");
        }

        public void Handle(ReplicationMessage.CommitPhaseTimeout message)
        {
            if (_completed || _awaitingCommit == 0 || _awaitingPrepare != 0)
                return;

            CompleteFailedRequest(message.CorrelationId, _eventStreamId, OperationErrorCode.CommitTimeout, "Commit phase timeout.");
        }


        public void Handle(ReplicationMessage.AlreadyCommitted message)
        {
            Debug.Assert(message.EventStreamId == _eventStreamId && message.CorrelationId == _correlationId);
            CompleteSuccessRequest(_correlationId, _eventStreamId, message.StartEventNumber);
        }

        public void Handle(ReplicationMessage.PrepareAck message)
        {
            if (_completed)
                return;

            if ((message.Flags & PrepareFlags.TransactionBegin) != 0)
                _preparePos = message.LogPosition;

            if ((message.Flags & PrepareFlags.TransactionEnd) != 0)
            {
                _awaitingPrepare -= 1;
                if (_awaitingPrepare == 0)
                {
                    Publisher.Publish(new ReplicationMessage.WriteCommit(message.CorrelationId, _publishEnvelope, _preparePos));
                    Publisher.Publish(TimerMessage.Schedule.Create(Timeouts.CommitTimeout,
                                                                   _publishEnvelope,
                                                                   new ReplicationMessage.CommitPhaseTimeout(_correlationId)));
                }
            }
        }

        public void Handle(ReplicationMessage.CommitAck message)
        {
            if (_completed)
                return;

            _awaitingCommit -= 1;
            if (_awaitingCommit == 0)
                CompleteSuccessRequest(message.CorrelationId, _eventStreamId, message.EventNumber);
        }

        protected virtual void CompleteSuccessRequest(Guid correlationId, string eventStreamId, int startEventNumber)
        {
            _completed = true;
            Publisher.Publish(new ReplicationMessage.RequestCompleted(correlationId, true));
        }

        protected virtual void CompleteFailedRequest(Guid correlationId, string eventStreamId, OperationErrorCode errorCode, string error)
        {
            Debug.Assert(errorCode != OperationErrorCode.Success);
            _completed = true;
            Publisher.Publish(new ReplicationMessage.RequestCompleted(correlationId, false));
        }
    }
}