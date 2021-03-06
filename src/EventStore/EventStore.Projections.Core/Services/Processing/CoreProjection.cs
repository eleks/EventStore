// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Text;
using EventStore.Common.Log;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Utils;

namespace EventStore.Projections.Core.Services.Processing
{
    //TODO: replace Console.WriteLine with logging
    //TODO: separate check-pointing from projection handling
    public class CoreProjection : IDisposable,
                                  ICoreProjection,
                                  IHandle<ProjectionMessage.Projections.CheckpointCompleted>,
                                  IHandle<ProjectionMessage.Projections.PauseRequested>,
                                  IHandle<ProjectionMessage.Projections.CommittedEventReceived>,
                                  IHandle<ProjectionMessage.Projections.CheckpointSuggested>
    {
        internal const string ProjectionsStreamPrefix = "$projections-";
        private const string ProjectionsStateStreamSuffix = "-state";
        internal const string ProjectionCheckpointStreamSuffix = "-checkpoint";

        [Flags]
        private enum State : uint
        {
            Initial = 0x80000000,
            LoadStateRequsted = 0x1,
            StateLoadedSubscribed = 0x2,
            Running = 0x08,
            Paused = 0x10,
            Resumed = 0x20,
            Stopping = 0x40,
            Stopped = 0x80,
            FaultedStopping = 0x100,
            Faulted = 0x200,
        }

        private readonly string _name;

        private readonly IPublisher _publisher;

        private readonly Guid _projectionCorrelationId;
        private readonly ProjectionConfig _projectionConfig;
        private readonly EventFilter _eventFilter;
        private readonly CheckpointStrategy _checkpointStrategy;
        private readonly ILogger _logger;

        private readonly IProjectionStateHandler _projectionStateHandler;
        private State _state;

        //NOTE: this queue hides the real length of projection stage incoming queue, so the almost empty stage queue may still handle many long projection queues

        //NOTE: this may note work well on recovery when reading from any index instead of replying all the event stream (index will likely render less events than original event stream)

        //TODO: join incheckpoint fields into single checkpoint state field
        private string _faultedReason;

        private readonly
            RequestResponseDispatcher
                <ClientMessage.ReadStreamEventsBackward, ClientMessage.ReadStreamEventsBackwardCompleted>
            _readDispatcher;

        private readonly RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted>
            _writeDispatcher;

        private string _handlerPartition;
        private readonly PartitionStateCache _partitionStateCache;
        private readonly CoreProjectionQueue _processingQueue;
        private readonly CoreProjectionCheckpointManager _checkpointManager;

        private bool _tickPending;
        private int _readRequestsInProgress;

        public CoreProjection(
            string name, Guid projectionCorrelationId, IPublisher publisher,
            IProjectionStateHandler projectionStateHandler, ProjectionConfig projectionConfig,
            RequestResponseDispatcher
                <ClientMessage.ReadStreamEventsBackward, ClientMessage.ReadStreamEventsBackwardCompleted> readDispatcher,
            RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted> writeDispatcher,
            ILogger logger = null)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == "") throw new ArgumentException("name");
            if (publisher == null) throw new ArgumentNullException("publisher");
            if (projectionStateHandler == null) throw new ArgumentNullException("projectionStateHandler");
            _projectionCorrelationId = projectionCorrelationId;
            _name = name;
            _projectionConfig = projectionConfig;
            _logger = logger;
            _publisher = publisher;
            _projectionStateHandler = projectionStateHandler;
            _readDispatcher = readDispatcher;
            _writeDispatcher = writeDispatcher;
            var builder = new CheckpointStrategy.Builder();
            _projectionStateHandler.ConfigureSourceProcessingStrategy(builder);
            _checkpointStrategy = builder.Build(_projectionConfig.Mode);
            _eventFilter = _checkpointStrategy.EventFilter;
            _partitionStateCache = new PartitionStateCache();
            _processingQueue = new CoreProjectionQueue(
                projectionCorrelationId, publisher, projectionConfig.PendingEventsThreshold, UpdateStatistics);
            _checkpointManager = this._checkpointStrategy.CreateCheckpointManager(
                this, projectionCorrelationId, this._publisher, this._readDispatcher,
                this._writeDispatcher, this._projectionConfig, this._name);
            GoToState(State.Initial);
        }

        internal void UpdateStatistics()
        {
            _publisher.Publish(
                new ProjectionMessage.Projections.Management.StatisticsReport(_projectionCorrelationId, GetStatistics()));
        }

        public void Start()
        {
            EnsureState(State.Initial);
            GoToState(State.LoadStateRequsted);
        }

        public void Stop()
        {
            EnsureState(State.StateLoadedSubscribed | State.Paused | State.Resumed | State.Running);
            GoToState(State.Stopping);
        }

        public string GetProjectionState()
        {
            //TODO: separate requesting valid only state (not catching-up, non-stopped etc)
            //EnsureState(State.StateLoadedSubscribed | State.Stopping | State.Subscribed | State.Paused | State.Resumed | State.Running);
            return _partitionStateCache.GetLockedPartitionState("");
        }

        private ProjectionStatistics GetStatistics()
        {
            var checkpointStatistics = _checkpointManager.GetStatistics();
            return new ProjectionStatistics
                {
                    Mode = _projectionConfig.Mode,
                    Name = _name,
                    Position = checkpointStatistics.Position,
                    StateReason = "",
                    Status = _state.EnumVaueName() + checkpointStatistics.Status + _processingQueue.GetStatus(),
                    LastCheckpoint = checkpointStatistics.LastCheckpoint,
                    EventsProcessedAfterRestart = checkpointStatistics.EventsProcessedAfterRestart,
                    BufferedEvents = _processingQueue.GetBufferedEventCount(),
                    WritePendingEventsBeforeCheckpoint = checkpointStatistics.WritePendingEventsBeforeCheckpoint,
                    WritePendingEventsAfterCheckpoint = checkpointStatistics.WritePendingEventsAfterCheckpoint,
                    ReadsInProgress = _readRequestsInProgress + checkpointStatistics.ReadsInProgress,
                    WritesInProgress = checkpointStatistics.WritesInProgress,
                    PartitionsCached = _partitionStateCache.CachedItemCount,
                    CheckpointStatus = checkpointStatistics.CheckpointStatus,
                };
        }

        public void Handle(ProjectionMessage.Projections.CommittedEventReceived message)
        {
            EnsureState(
                State.Running | State.Paused | State.Stopping | State.Stopped | State.FaultedStopping | State.Faulted);
            try
            {
                if (_state == State.Running || _state == State.Paused)
                {
                    CheckpointTag eventTag = message.CheckpointTag;
                    string partition = _checkpointStrategy.StatePartitionSelector.GetStatePartition(message);
                    var committedEventWorkItem = new CommittedEventWorkItem(this, message, partition);
                    _processingQueue.EnqueueTask(committedEventWorkItem, eventTag);
                }
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.Projections.CheckpointSuggested message)
        {
            EnsureState(
                State.Running | State.Paused | State.Stopping | State.Stopped | State.FaultedStopping | State.Faulted);
            try
            {
                if ((_state == State.Running || _state == State.Paused) && _projectionConfig.CheckpointsEnabled)
                {
                    CheckpointTag checkpointTag = message.CheckpointTag;
                    var checkpointSuggestedWorkItem = new CheckpointSuggestedWorkItem(this, message, _checkpointManager);
                    _processingQueue.EnqueueTask(checkpointSuggestedWorkItem, checkpointTag);
                }
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.Projections.CheckpointCompleted message)
        {
            CheckpointCompleted(message.CheckpointTag);
        }

        public void Handle(ProjectionMessage.Projections.PauseRequested message)
        {
            Pause();
        }

        private void GoToState(State state)
        {
            var wasStopped = _state == State.Stopped || _state == State.Faulted;
            var wasStopping = _state == State.Stopping || _state == State.FaultedStopping;
            var wasStarted = _state == State.StateLoadedSubscribed || _state == State.Paused || _state == State.Resumed
                             || _state == State.Running || _state == State.Stopping || _state == State.FaultedStopping;
            _state = state; // set state before transition to allow further state change
            switch (state)
            {
                case State.Running:
                    _processingQueue.SetRunning();
                    break;
                case State.Paused:
                    _processingQueue.SetPaused();
                    break;
                default:
                    _processingQueue.SetStopped();
                    break;
            }
            switch (state)
            {
                case State.Stopped:
                case State.Faulted:
                    if (wasStarted && !wasStopped)
                        _checkpointManager.Stopped();
                    break;
                case State.Stopping:
                case State.FaultedStopping:
                    if (wasStarted && !wasStopping)
                        _checkpointManager.Stopping();
                    break;
            }
            switch (state)
            {
                case State.Initial:
                    EnterInitial();
                    break;
                case State.LoadStateRequsted:
                    EnterLoadStateRequested();
                    break;
                case State.StateLoadedSubscribed:
                    EnterStateLoadedSubscribed();
                    break;
                case State.Running:
                    EnterRunning();
                    break;
                case State.Paused:
                    EnterPaused();
                    break;
                case State.Resumed:
                    EnterResumed();
                    break;
                case State.Stopping:
                    EnterStopping();
                    break;
                case State.Stopped:
                    EnterStopped();
                    break;
                case State.FaultedStopping:
                    EnterFaultedStopping();
                    break;
                case State.Faulted:
                    EnterFaulted();
                    break;
                default:
                    throw new Exception();
            }
        }

        private void EnterInitial()
        {
            _partitionStateCache.CacheAndLockPartitionState("", "", null);
            // NOTE: this is to workaround exception in GetState requests submitted by client
        }

        private void EnterLoadStateRequested()
        {
            _checkpointManager.BeginLoadState();
        }

        private void EnterStateLoadedSubscribed()
        {
            GoToState(State.Running);
        }

        private void EnterRunning()
        {
            try
            {
                UpdateStatistics();
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        private void EnterPaused()
        {
        }

        private void EnterResumed()
        {
            GoToState(State.Running);
        }

        private void EnterStopping()
        {
            Console.WriteLine("Stopping");
            _publisher.Publish(new ProjectionMessage.Projections.UnsubscribeProjection(_projectionCorrelationId));
            // core projection may be stopped to change its configuration
            // it is important to checkpoint it so no writes pending remain when stopped
            _checkpointManager.RequestCheckpointToStop(); // should always report complted even if skipped
        }

        private void EnterStopped()
        {
            _publisher.Publish(new ProjectionMessage.Projections.StatusReport.Stopped(_projectionCorrelationId));
        }

        private void EnterFaultedStopping()
        {
            _publisher.Publish(new ProjectionMessage.Projections.UnsubscribeProjection(_projectionCorrelationId));
            // checkpoint last known correct state on fault
            _checkpointManager.RequestCheckpointToStop(); // should always report complted even if skipped
        }

        private void EnterFaulted()
        {
            _publisher.Publish(
                new ProjectionMessage.Projections.StatusReport.Faulted(_projectionCorrelationId, _faultedReason));
        }

        private void SetHandlerState(string partition)
        {
            if (_handlerPartition == partition)
                return;
            string newState = _partitionStateCache.GetLockedPartitionState(partition);
            _handlerPartition = partition;
            if (!string.IsNullOrEmpty(newState))
                _projectionStateHandler.Load(newState);
            else
                _projectionStateHandler.Initialize();
        }

        private void LoadProjectionStateFaulted(string newState, Exception ex)
        {
            _faultedReason =
                string.Format(
                    "Cannot load the {0} projection state.\r\nHandler: {1}\r\nState:\r\n\r\n{2}\r\n\r\nMessage:\r\n\r\n{3}",
                    _name, GetHandlerTypeName(), newState, ex.Message);
            if (_logger != null)
                _logger.ErrorException(ex, _faultedReason);
            GoToState(State.Faulted);
        }

        private string GetHandlerTypeName()
        {
            return _projectionStateHandler.GetType().Namespace + "." + _projectionStateHandler.GetType().Name;
        }

        private void TryResume()
        {
            GoToState(State.Resumed);
        }

        internal void ProcessCommittedEvent(
            CommittedEventWorkItem committedEventWorkItem, ProjectionMessage.Projections.CommittedEventReceived message,
            string partition)
        {
            if (message.Data == null)
                throw new NotSupportedException();

            EnsureState(State.Running);
            InternalProcessCommittedEvent(committedEventWorkItem, partition, message);
        }

        private void InternalProcessCommittedEvent(
            CommittedEventWorkItem committedEventWorkItem, string partition,
            ProjectionMessage.Projections.CommittedEventReceived message)
        {
            string newState = null;
            EmittedEvent[] emittedEvents = null;

            //TODO: not emitting (optimized) projection handlers can skip serializing state on each processed event
            bool hasBeenProcessed;
            try
            {
                hasBeenProcessed = ProcessEventByHandler(partition, message, out newState, out emittedEvents);
            }
            catch (Exception ex)
            {
                ProcessEventFaulted(
                    string.Format(
                        "The {0} projection failed to process an event.\r\nHandler: {1}\r\nEvent Position: {2}\r\n\r\nMessage:\r\n\r\n{3}",
                        _name, GetHandlerTypeName(), message.Position, ex.Message), ex);
                newState = null;
                emittedEvents = null;
                hasBeenProcessed = false;
            }
            newState = newState ?? "";
            if (hasBeenProcessed)
            {
                if (!ProcessEmittedEvents(committedEventWorkItem, emittedEvents))
                    return;

                if (_partitionStateCache.GetLockedPartitionState(partition) != newState)
                    // ensure state actually changed
                {
                    var lockPartitionStateAt = partition != "" ? message.CheckpointTag : null;
                    _partitionStateCache.CacheAndLockPartitionState(partition, newState, lockPartitionStateAt);
                    if (_projectionConfig.PublishStateUpdates)
                        EmitStateUpdated(committedEventWorkItem, partition, newState);
                }
            }
        }

        private bool ProcessEmittedEvents(CommittedEventWorkItem committedEventWorkItem, EmittedEvent[] emittedEvents)
        {
            if (_projectionConfig.EmitEventEnabled)
                EmitEmittedEvents(committedEventWorkItem, emittedEvents);
            else if (emittedEvents != null && emittedEvents.Length > 0)
            {
                ProcessEventFaulted("emit_event is not enabled by the projection configuration/mode");
                return false;
            }
            return true;
        }

        private bool ProcessEventByHandler(
            string partition, ProjectionMessage.Projections.CommittedEventReceived message, out string newState,
            out EmittedEvent[] emittedEvents)
        {
            SetHandlerState(partition);
            return _projectionStateHandler.ProcessEvent(
                message.Position, message.EventStreamId, message.Data.EventType,
                _eventFilter.GetCategory(message.PositionStreamId), message.Data.EventId, message.EventSequenceNumber,
                Encoding.UTF8.GetString(message.Data.Metadata), Encoding.UTF8.GetString(message.Data.Data), out newState,
                out emittedEvents);
        }

        private void EmitEmittedEvents(CommittedEventWorkItem committedEventWorkItem, EmittedEvent[] emittedEvents)
        {
            bool result = emittedEvents != null && emittedEvents.Length > 0;
            if (result)
                committedEventWorkItem.ScheduleEmitEvents(emittedEvents);
        }

        private void EmitStateUpdated(CommittedEventWorkItem committedEventWorkItem, string partition, string newState)
        {
            committedEventWorkItem.ScheduleEmitEvents(
                new[]
                    {
                        new EmittedEvent(MakePartitionStateStreamName(partition), Guid.NewGuid(), "StateUpdated", newState)
                    });
        }

        private void ProcessEventFaulted(string faultedReason, Exception ex = null)
        {
            _faultedReason = faultedReason;
            if (_logger != null)
            {
                if (ex != null)
                    _logger.ErrorException(ex, _faultedReason);
                else
                    _logger.Error(_faultedReason);
            }
            GoToState(State.FaultedStopping);
        }

        private void EnsureState(State expectedStates)
        {
            if ((_state & expectedStates) == 0)
            {
                throw new Exception(
                    string.Format("Current state is {0}. Expected states are: {1}", _state, expectedStates));
            }
        }

        private void Tick()
        {
            EnsureState(State.Running | State.Paused | State.Stopping | State.FaultedStopping | State.Faulted);
            // we may get into faulted any time, so it is allowed
            try
            {
                _tickPending = false;
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.Projections.CheckpointLoaded message)
        {
            EnsureState(State.LoadStateRequsted);
            OnLoadStateCompleted(message.CheckpointTag, message.CheckpointData);
            GoToState(State.StateLoadedSubscribed);
        }

        private void OnLoadStateCompleted(CheckpointTag checkpointTag, string checkpointData)
        {
            if (checkpointTag == null)
            {
                var zeroTag = _checkpointStrategy.PositionTagger.MakeZeroCheckpointTag();
                InitializeProjectionFromCheckpoint("", zeroTag);
            }
            else
            {
                InitializeProjectionFromCheckpoint(checkpointData, checkpointTag);
            }
        }

        private void InitializeProjectionFromCheckpoint(string state, CheckpointTag checkpointTag)
        {
            EnsureState(State.Initial | State.LoadStateRequsted);
            //TODO: initialize projection state here (test it)
            //TODO: write test to ensure projection state is correctly loaded from a checkpoint and posted back when enough empty records processed
            _partitionStateCache.CacheAndLockPartitionState("", state, null);
            _checkpointManager.Start(checkpointTag);
            try
            {
                SetHandlerState("");
                GoToState(State.StateLoadedSubscribed);
            }
            catch (Exception ex)
            {
                LoadProjectionStateFaulted(state, ex);
                return;
            }
            _processingQueue.InitializeQueue(_checkpointStrategy.PositionTagger.MakeZeroCheckpointTag());
            _publisher.Publish(
                new ProjectionMessage.Projections.SubscribeProjection(
                    _projectionCorrelationId, this, checkpointTag, _checkpointStrategy,
                    _projectionConfig.CheckpointUnhandledBytesThreshold));
            _publisher.Publish(new ProjectionMessage.Projections.StatusReport.Started(_projectionCorrelationId));
        }

        internal void BeginStatePartitionLoad(
            ProjectionMessage.Projections.CommittedEventReceived @event, Action loadCompleted)
        {
            string statePartition = _checkpointStrategy.StatePartitionSelector.GetStatePartition(@event);
            if (statePartition == "") // root is always cached
            {
                loadCompleted();
                return;
            }
            string state = _partitionStateCache.TryGetAndLockPartitionState(statePartition, @event.CheckpointTag);
            if (state != null)
                loadCompleted();
            else
            {
                string partitionStateStreamName = MakePartitionStateStreamName(statePartition);
                _readRequestsInProgress++;
                _readDispatcher.Publish(
                    new ClientMessage.ReadStreamEventsBackward(
                        Guid.NewGuid(), _readDispatcher.Envelope, partitionStateStreamName, -1, 1, resolveLinks: false),
                    m => OnLoadStatePartitionCompleted(statePartition, @event, m, loadCompleted));
            }
        }

        private string MakePartitionStateStreamName(string statePartition)
        {
            return ProjectionsStreamPrefix + _name + (string.IsNullOrEmpty(statePartition) ? "" : "-") + statePartition
                   + ProjectionsStateStreamSuffix;
        }

        private void OnLoadStatePartitionCompleted(
            string partition, ProjectionMessage.Projections.CommittedEventReceived committedEventReceived,
            ClientMessage.ReadStreamEventsBackwardCompleted message, Action loadCompleted)
        {
            _readRequestsInProgress--;
            var positionTag = committedEventReceived.CheckpointTag;
            if (message.Events.Length == 1)
            {
                EventRecord @event = message.Events[0].Event;
                if (@event.EventType == "StateUpdated")
                {
                    var checkpointTag = @event.Metadata.ParseJson<CheckpointTag>();
                    // always recovery mode? skip until state before current event
                    //TODO: skip event processing in case we know i has been already processed
                    CheckpointTag eventPositionTag = positionTag;
                    if (checkpointTag < eventPositionTag)
                    {
                        _partitionStateCache.CacheAndLockPartitionState(
                            partition, Encoding.UTF8.GetString(@event.Data), eventPositionTag);
                        loadCompleted();
                        EnsureTickPending();
                        return;
                    }
                }
            }
            if (message.NextEventNumber == -1)
            {
                _partitionStateCache.CacheAndLockPartitionState(partition, "", positionTag);
                loadCompleted();
                EnsureTickPending();
                return;
            }
            string partitionStateStreamName = MakePartitionStateStreamName(partition);
            _readRequestsInProgress++;
            _readDispatcher.Publish(
                new ClientMessage.ReadStreamEventsBackward(
                    Guid.NewGuid(), _readDispatcher.Envelope, partitionStateStreamName, message.NextEventNumber, 1,
                    resolveLinks: false),
                m => OnLoadStatePartitionCompleted(partition, committedEventReceived, m, loadCompleted));
        }

        public void Dispose()
        {
            if (_projectionStateHandler != null)
                _projectionStateHandler.Dispose();
        }

        internal void EnsureTickPending()
        {
            if (_tickPending)
                return;
            if (_state == State.Paused)
                return;
            _tickPending = true;
            _publisher.Publish(new ProjectionMessage.CoreService.Tick(Tick));
        }

        private void SetFaulted(Exception ex)
        {
            _faultedReason = ex.Message;
            GoToState(State.Faulted);
        }

        private void CheckpointCompleted(CheckpointTag lastCompletedCheckpointPosition)
        {
            // all emitted events caused by events before the checkpoint position have been written  
            // unlock states, so the cache can be clean up as they can now be safely reloaded from the ES
            _partitionStateCache.Unlock(lastCompletedCheckpointPosition);

            switch (_state)
            {
                case State.Paused:
                    TryResume();
                    break;
                case State.Stopping:
                    GoToState(State.Stopped);
                    break;
                case State.FaultedStopping:
                    GoToState(State.Faulted);
                    break;
            }
        }

        private void Pause()
        {
            if (_state != State.Stopping && _state != State.FaultedStopping)
                // stopping projection is already paused
                GoToState(State.Paused);
        }

        internal void FinalizeEventProcessing(
            List<EmittedEvent[]> scheduledWrites,
            ProjectionMessage.Projections.CommittedEventReceived committedEventReceived)
        {
            if (committedEventReceived.Data == null)
                throw new NotSupportedException();
            if (_state != State.Faulted && _state != State.FaultedStopping)
            {
                EnsureState(State.Running);
                //TODO: move to separate projection method and cache result in work item
                var checkpointTag = committedEventReceived.CheckpointTag;
                _checkpointManager.EventProcessed(GetProjectionState(), scheduledWrites, checkpointTag);
            }
        }
    }
}
