using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTDB.ARTLib;

namespace BTDB.KVDBLayer
{
    public class ArtInMemoryKeyValueDB : IKeyValueDB
    {
        IRootNode _lastCommited;
        ArtInMemoryKeyValueDBTransaction _writingTransaction;
        readonly Queue<TaskCompletionSource<IKeyValueDBTransaction>> _writeWaitingQueue = new Queue<TaskCompletionSource<IKeyValueDBTransaction>>();
        readonly object _writeLock = new object();
        readonly ConcurrentBag<IRootNode> _waitingToDispose = new ConcurrentBag<IRootNode>(); 
        
        public ArtInMemoryKeyValueDB(IOffHeapAllocator allocator)
        {
            _lastCommited = ARTImpl.CreateEmptyRoot(allocator, false);
        }

        public void Dispose()
        {
            lock (_writeLock)
            {
                if (_writingTransaction != null) throw new BTDBException("Cannot dispose KeyValueDB when writting transaction still running");
                while (_writeWaitingQueue.Count > 0)
                {
                    _writeWaitingQueue.Dequeue().TrySetCanceled();
                }
                DereferenceRoot(_lastCommited);
                FreeWaitingToDispose();
            }
        }

        public bool DurableTransactions { get; set; }

        internal IRootNode LastCommited => _lastCommited;

        public IKeyValueDBTransaction StartTransaction()
        {
            var node = LastCommited;
            node.Reference();
            return new ArtInMemoryKeyValueDBTransaction(this, node, false, false);
        }

        public IKeyValueDBTransaction StartReadOnlyTransaction()
        {
            var node = LastCommited;
            node.Reference();
            return new ArtInMemoryKeyValueDBTransaction(this, node, false, true);
        }

        public Task<IKeyValueDBTransaction> StartWritingTransaction()
        {
            lock (_writeLock)
            {
                var tcs = new TaskCompletionSource<IKeyValueDBTransaction>();
                if (_writingTransaction == null)
                {
                    NewWrittingTransactionUnsafe(tcs);
                }
                else
                {
                    _writeWaitingQueue.Enqueue(tcs);
                }
                return tcs.Task;
            }
        }

        public string CalcStats()
        {
            return "KeyValueCount:" + LastCommited.GetCount() + Environment.NewLine;
        }

        public bool Compact(CancellationToken cancellation)
        {
            return false;
        }

        public IKeyValueDBLogger Logger { get; set; }

        public ulong? PreserveHistoryUpToCommitUlong {
            get { return null; }
            set { /* ignore */ }
        }

        internal IRootNode MakeWrittableTransaction(ArtInMemoryKeyValueDBTransaction keyValueDBTransaction, IRootNode artRoot)
        {
            lock (_writeLock)
            {
                if (_writingTransaction != null) throw new BTDBTransactionRetryException("Another writting transaction already running");
                if (LastCommited != artRoot) throw new BTDBTransactionRetryException("Another writting transaction already finished");
                _writingTransaction = keyValueDBTransaction;
                var result = _lastCommited;
                _lastCommited = result.Snapshot();
                artRoot.Dereference();
                return result;
            }
        }

        internal void CommitWrittingTransaction(IRootNode artRoot)
        {
            lock (_writeLock)
            {
                _writingTransaction = null;
                if (_lastCommited.Dereference())
                {
                    _lastCommited.Dispose();
                }
                _lastCommited = artRoot;
                TryDequeWaiterForWrittingTransaction();
            }
        }

        void TryDequeWaiterForWrittingTransaction()
        {
            FreeWaitingToDispose();
            if (_writeWaitingQueue.Count == 0) return;
            var tcs = _writeWaitingQueue.Dequeue();
            NewWrittingTransactionUnsafe(tcs);
        }

        void NewWrittingTransactionUnsafe(TaskCompletionSource<IKeyValueDBTransaction> tcs)
        {
            FreeWaitingToDispose();
            var newTransactionRoot = LastCommited;
            _lastCommited = newTransactionRoot.Snapshot();
            _writingTransaction = new ArtInMemoryKeyValueDBTransaction(this, newTransactionRoot, true, false);
            tcs.TrySetResult(_writingTransaction);
        }

        void FreeWaitingToDispose()
        {
            while (_waitingToDispose.TryTake(out var node))
            {
                node.Dispose();
            }
        }

        internal void RevertWrittingTransaction(IRootNode currentArtRoot)
        {
            lock (_writeLock)
            {
                currentArtRoot.RevertTo(_lastCommited);
                if (_lastCommited.Dereference())
                {
                    _lastCommited.Dispose();
                }
                _lastCommited = currentArtRoot;
                _writingTransaction = null;
                TryDequeWaiterForWrittingTransaction();
            }
        }

        internal void DereferenceRoot(IRootNode currentArtRoot)
        {
            if (currentArtRoot.Dereference())
            {
                _waitingToDispose.Add(currentArtRoot);
            }
        }
    }
}