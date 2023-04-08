using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using JetBrains.Annotations;

namespace Anatawa12.VpmPackageAutoInstaller
{
    [AsyncMethodBuilder(typeof(SyncedTask<>))]
    public class SyncedTask<TResult>
    {
        private IAsyncStateMachine _stateMachine;
        private bool _end = false;
        private TResult _result;
        private Exception _exception;
        private SynchronizationContextImpl _contextImpl = new SynchronizationContextImpl();

        [UsedImplicitly] // async method builder
        public static SyncedTask<TResult> Create() => new SyncedTask<TResult>();

        [UsedImplicitly] // async method builder
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            SynchronizationContext.SetSynchronizationContext(_contextImpl);
            stateMachine.MoveNext();
        }

        [UsedImplicitly] // async method builder
        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            _stateMachine = stateMachine;
        }

        [UsedImplicitly] // async method builder
        public void SetResult(TResult result)
        {
            _result = result;
            _contextImpl.Finish();
        }

        [UsedImplicitly] // async method builder
        public void SetException(Exception exception)
        {
            _exception = exception;
            _contextImpl.Finish();
        }

        [UsedImplicitly] // async method builder
        public SyncedTask<TResult> Task => this;

        [UsedImplicitly] // async method builder
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            if (_stateMachine == null)
            {
                _stateMachine = stateMachine;
                _stateMachine.SetStateMachine(_stateMachine);
            }

            SynchronizationContext.SetSynchronizationContext(_contextImpl);
            awaiter.OnCompleted(() => _contextImpl.Post(OnCompleted, this));
        }

        [UsedImplicitly] // async method builder
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter,
            ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            if (_stateMachine == null)
            {
                _stateMachine = stateMachine;
                _stateMachine.SetStateMachine(_stateMachine);
            }

            SynchronizationContext.SetSynchronizationContext(_contextImpl);
            awaiter.UnsafeOnCompleted(() => _contextImpl.Post(OnCompleted, this));
        }

        private static void OnCompleted(object state)
        {
            var self = (SyncedTask<TResult>)state;
            
            SynchronizationContext.SetSynchronizationContext(self._contextImpl);
            self._stateMachine.MoveNext();
        }

        public TResult Execute()
        {
            _contextImpl.Execute();

            if (_exception != null)
                throw _exception;
            return _result;
        }
    }

    class SynchronizationContextImpl : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback d, object state)> _queue =
            new BlockingCollection<(SendOrPostCallback, object)>();

        public override void Post(SendOrPostCallback d, object state)
        {
            _queue.Add((d, state));
        }

        public void Finish() => _queue.CompleteAdding();

        public void Execute()
        {
            foreach (var (d, state) in _queue.GetConsumingEnumerable())
            {
                SetSynchronizationContext(this);
                d(state);
            }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    sealed class AsyncMethodBuilderAttribute : Attribute
    {
        public AsyncMethodBuilderAttribute(Type builderType)
        {
            BuilderType = builderType;
        }

        public Type BuilderType { get; }
    }
}
