﻿using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Providers;
using System.Collections.Concurrent;
using System.Threading;
using System.Runtime.CompilerServices;

namespace Orleans.Indexing
{
    /// <summary>
    /// A simple implementation of a single-grain in-memory hash-index
    /// </summary>
    /// <typeparam name="K">type of hash-index key</typeparam>
    /// <typeparam name="V">type of grain that is being indexed</typeparam>
    [Reentrant]
    public abstract class HashIndexSingleBucket<K, V> : Grain<HashIndexBucketState<K,V>>, HashIndexSingleBucketInterface<K, V> where V : class, IIndexableGrain
    {
        //private Func<K, K, bool> _equalsLambda = ((k1,k2) => k1.Equals(k2));
        //private Func<K, long> _hashLambda = (k => k.GetHashCode());

        //private static ConcurrentDictionary<string, int> indexMetaData; //maps an index-full-name to its meta-data, which currently only consists of the limit of the number of elements in a single bucket

        private static readonly Logger logger = LogManager.GetLogger(string.Format("HashIndexSingleBucket<{0},{1}>", typeof(K).Name, typeof(V).Name), LoggerType.Grain);

        public override Task OnActivateAsync()
        {
            //await ReadStateAsync();
            if (State.IndexMap == null) State.IndexMap = new Dictionary<K, HashIndexSingleBucketEntry<V>>();
            State.IndexStatus = IndexStatus.Available;
            //if (State.IndexStatus == IndexStatus.UnderConstruction)
            //{
            //    var _ = GetIndexBuilder().BuildIndex(indexName, this, IndexUtils.GetIndexUpdateGenerator<V>(GrainFactory, IndexUtils.GetIndexNameFromIndexGrain(this)));
            //}
            write_lock = new AsyncLock();
            writeRequestIdGen = 0;
            pendingWriteRequests = new HashSet<int>();
            return base.OnActivateAsync();
        }

        #region Multi-threaded Index Update
        #region Multi-threaded Index Update Variables

        /// <summary>
        /// This lock is used to queue all the writes to the storage
        /// and do them in a single batch, i.e., group commit
        /// 
        /// Works hand-in-hand with pendingWriteRequests and writeRequestIdGen.
        /// </summary>
        private AsyncLock write_lock;

        /// <summary>
        /// Creates a unique ID for each write request to the storage.
        /// 
        /// The values generated by this ID generator are used in pendingWriteRequests
        /// </summary>
        private int writeRequestIdGen;

        /// <summary>
        /// All the write requests that are waiting behind write_lock are accumulated
        /// in this data structure, and all of them will be done at once.
        /// </summary>
        private HashSet<int> pendingWriteRequests;

        #endregion Multi-threaded Index Update Variables

        public async Task<bool> DirectApplyIndexUpdateBatch(Immutable<IDictionary<IIndexableGrain, IList<IMemberUpdate>>> iUpdates, bool isUnique, IndexMetaData idxMetaData, SiloAddress siloAddress = null)
        {
            //if (idxMetaData.IsCreatingANewBucketNecessary(State.IndexMap.Count()))
            //{
            //    return await (await GetNextBucketAndPersist()).DirectApplyIndexUpdateBatch(iUpdates, isUnique, idxMetaData, siloAddress);
            //}
            //else
            //{
                if (logger.IsVerbose) logger.Verbose("Started calling DirectApplyIndexUpdateBatch with the following parameters: isUnique = {0}, siloAddress = {1}, iUpdates = {2}", isUnique, siloAddress, MemberUpdate.UpdatesToString(iUpdates.Value));

                IDictionary<IIndexableGrain, IList<IMemberUpdate>> updates = iUpdates.Value;
                Task[] updateTasks = new Task[updates.Count()];
                int i = 0;
                foreach (var kv in updates)
                {
                    updateTasks[i] = DirectApplyIndexUpdatesNonPersistent(kv.Key, kv.Value, isUnique, idxMetaData, siloAddress);
                    ++i;
                }
                await Task.WhenAll(updateTasks);
                await PersistIndex();

                if (logger.IsVerbose) logger.Verbose("Finished calling DirectApplyIndexUpdateBatch with the following parameters: isUnique = {0}, siloAddress = {1}, iUpdates = {2}", isUnique, siloAddress, MemberUpdate.UpdatesToString(iUpdates.Value));

                return true;
            //}
        }

        private async Task<IndexInterface<K, V>> GetNextBucketAndPersist()
        {
            IndexInterface<K, V> nextBucket = GetNextBucket();
            await PersistIndex();
            return nextBucket;
        }

        internal abstract IndexInterface<K, V> GetNextBucket();

        /// <summary>
        /// This method applies a given update to the current index.
        /// </summary>
        /// <param name="updatedGrain">the grain that issued the update</param>
        /// <param name="iUpdate">contains the data for the update</param>
        /// <param name="isUnique">whether this is a unique index that we are updating</param>
        /// <param name="op">the actual type of the operation, which override the operation-type in iUpdate</param>
        /// <returns>true, if the index update was successful, otherwise false</returns>
        public async Task<bool> DirectApplyIndexUpdate(IIndexableGrain g, Immutable<IMemberUpdate> iUpdate, bool isUniqueIndex, IndexMetaData idxMetaData, SiloAddress siloAddress)
        {
            //if (idxMetaData.IsCreatingANewBucketNecessary(State.IndexMap.Count()))
            //{
            //    return await (await GetNextBucketAndPersist()).DirectApplyIndexUpdate(g, iUpdate, isUniqueIndex, idxMetaData, siloAddress);
            //}
            //else
            //{
                await DirectApplyIndexUpdateNonPersistent(g, iUpdate.Value, isUniqueIndex, idxMetaData, siloAddress);
                await PersistIndex();
                return true;
            //}
        }

        private Task DirectApplyIndexUpdatesNonPersistent(IIndexableGrain g, IList<IMemberUpdate> updates, bool isUniqueIndex, IndexMetaData idxMetaData, SiloAddress siloAddress)
        {
            Task[] updateTasks = new Task[updates.Count()];
            int i = 0;
            foreach(IMemberUpdate updt in updates)
            {
                updateTasks[i++] = DirectApplyIndexUpdateNonPersistent(g, updt, isUniqueIndex, idxMetaData, siloAddress);
            }
            return Task.WhenAll(updateTasks);
        }

        private async Task DirectApplyIndexUpdateNonPersistent(IIndexableGrain g, IMemberUpdate updt, bool isUniqueIndex, IndexMetaData idxMetaData, SiloAddress siloAddress)
        {
            //the index can start processing update as soon as it becomes
            //visible to index handler and does not have to wait for any
            //further event regarding index builder, so it is not necessary
            //to have a Created state
            //if (State.IndexStatus == IndexStatus.Created) return true;

            //this variable determines whether index was still unavailable
            //when we received a delete operation
            bool fixIndexUnavailableOnDelete = false;

            //the target grain that is updated
            V updatedGrain = g.AsReference<V>(GrainFactory);

            K befImg;
            HashIndexSingleBucketEntry<V> befEntry;

            //Updates the index bucket synchronously
            //(note that no other thread can run concurrently
            //before we reach an await operation, so no concurrency
            //control mechanism (e.g., locking) is required)
            if (!HashIndexBucketUtils.UpdateBucket(updatedGrain, updt, State, isUniqueIndex, idxMetaData, out befImg, out befEntry, out fixIndexUnavailableOnDelete))
            {
                await (await GetNextBucketAndPersist()).DirectApplyIndexUpdate(g, updt.AsImmutable(), isUniqueIndex, idxMetaData, siloAddress);
            }
            
            //if the index was still unavailable
            //when we received a delete operation
            if (fixIndexUnavailableOnDelete)
            {
                State.IndexStatus = await GetIndexBuilder().AddTombstone(updatedGrain) ? IndexStatus.Available : State.IndexStatus;
                if (State.IndexMap.TryGetValue(befImg, out befEntry) && befEntry.Values.Contains(updatedGrain))
                {
                    befEntry.Values.Remove(updatedGrain);
                    var isAvailable = await GetIndexBuilder().AddTombstone(updatedGrain);
                    if (State.IndexStatus != IndexStatus.Available && isAvailable)
                    {
                        State.IndexStatus = IndexStatus.Available;
                    }
                }
            }
        }

        /// <summary>
        /// Persists the state of the index
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task PersistIndex()
        {
            //create a write-request ID, which is used for group commit
            int writeRequestId = ++writeRequestIdGen;

            //add the write-request ID to the pending write requests
            pendingWriteRequests.Add(writeRequestId);

            //wait before any previous write is done
            using (await write_lock.LockAsync())
            {
                //if the write request was not already handled
                //by a previous group write attempt
                if (pendingWriteRequests.Contains(writeRequestId))
                {
                    //clear all pending write requests, as this attempt will do them all.
                    pendingWriteRequests.Clear();
                    //write the index state back to the storage
                    //TODO: What is the best way to handle an index write error?
                    int numRetries = 0;
                    while (true)
                    {
                        try
                        {
                            await base.WriteStateAsync();
                            return;
                        }
                        catch
                        {
                            if (numRetries++ > 3) throw;
                            await Task.Delay(100);
                        }
                    }
                }
                //else
                //{
                //    Nothing! It's already been done by a previous worker.
                //}
            }
        }

        private IIndexBuilder<V> GetIndexBuilder()
        {
            return GrainFactory.GetGrain<IIndexBuilder<V>>(this.GetPrimaryKeyString());
        }
        #endregion Multi-threaded Index Update

        //public Task<bool> IsUnique()
        //{
        //    return Task.FromResult(State.IsUnique);
        //}

        public async Task Lookup(IOrleansQueryResultStream<V> result, K key)
        {
            if (logger.IsVerbose) logger.Verbose("Streamed index lookup called for key = {0}", key);

            if (!(State.IndexStatus == IndexStatus.Available))
            {
                var e = new Exception(string.Format("Index is not still available."));
                GetLogger().Log((int)ErrorCode.IndexingIndexIsNotReadyYet, Severity.Error, "Index is not still available.", null, e);
                throw e;
            }
            HashIndexSingleBucketEntry<V> entry;
            if (State.IndexMap.TryGetValue(key, out entry) && !entry.isTentative())
            {
                await result.OnNextBatchAsync(entry.Values);
                await result.OnCompletedAsync();
            }
            else if(State.NextBucket != null)
            {
                await GetNextBucket().Lookup(result, key);
            }
            else
            {
                await result.OnCompletedAsync();
            }
        }

        public async Task<V> LookupUnique(K key)
        {
            if (!(State.IndexStatus == IndexStatus.Available))
            {
                var e = new Exception(string.Format("Index is not still available."));
                GetLogger().Error((int)ErrorCode.IndexingIndexIsNotReadyYet, e.Message, e);
                throw e;
            }
            HashIndexSingleBucketEntry<V> entry;
            if (State.IndexMap.TryGetValue(key, out entry) && !entry.isTentative())
            {
                if (entry.Values.Count() == 1)
                {
                    return entry.Values.GetEnumerator().Current;
                }
                else
                {
                    var e = new Exception(string.Format("There are {0} values for the unique lookup key \"{1}\" does not exist on index \"{2}\".", entry.Values.Count(), key, IndexUtils.GetIndexNameFromIndexGrain(this)));
                    GetLogger().Error((int)ErrorCode.IndexingIndexIsNotReadyYet, e.Message, e);
                    throw e;
                }
            }
            else if (State.NextBucket != null)
            {
                return await ((HashIndexInterface<K,V>) GetNextBucket()).LookupUnique(key);
            }
            else
            {
                var e = new Exception(string.Format("The lookup key \"{0}\" does not exist on index \"{1}\".", key, IndexUtils.GetIndexNameFromIndexGrain(this)));
                GetLogger().Error((int)ErrorCode.IndexingIndexIsNotReadyYet, e.Message, e);
                throw e;
            }
        }

        public Task Dispose()
        {
            State.IndexStatus = IndexStatus.Disposed;
            State.IndexMap.Clear();
            Runtime.DeactivateOnIdle(this);
            return TaskDone.Done;
        }

        public async Task<bool> IsAvailable()
        {
            //if (State.IndexStatus == IndexStatus.Available) return true;
            //var isDone = await GetIndexBuilder().IsDone();
            //if(isDone)
            //{
            //    State.IndexStatus = IndexStatus.Available;
            //    await base.WriteStateAsync();
            //    return true;
            //}
            return true;
        }

        Task IndexInterface.Lookup(IOrleansQueryResultStream<IIndexableGrain> result, object key)
        {
            return Lookup(result.Cast<V>(), (K)key);
        }

        public async Task<IOrleansQueryResult<V>> Lookup(K key)
        {
            if (logger.IsVerbose) logger.Verbose("Eager index lookup called for key = {0}", key);

            if (!(State.IndexStatus == IndexStatus.Available))
            {
                var e = new Exception(string.Format("Index is not still available."));
                GetLogger().Error((int)ErrorCode.IndexingIndexIsNotReadyYet, "Index is not still available.", e);
                throw e;
            }
            HashIndexSingleBucketEntry<V> entry;
            if (State.IndexMap.TryGetValue(key, out entry) && !entry.isTentative())
            {
                return new OrleansQueryResult<V>(entry.Values);
            }
            else if (State.NextBucket != null)
            {
                return await GetNextBucket().Lookup(key);
            }
            else
            {
                return new OrleansQueryResult<V>(Enumerable.Empty<V>());
            }
        }

        async Task<IOrleansQueryResult<IIndexableGrain>> IndexInterface.Lookup(object key)
        {
            return await Lookup((K)key);
        }

        /// <summary>
        /// Each hash-index needs a hash function, and a user can specify
        /// the hash function via a call to this method.
        /// 
        /// This method should be used internally by the index grain and
        /// should not be invoked from other grains.
        /// </summary>
        /// <param name="hashLambda">hash function that should be used
        /// for this hash-index</param>
        //void SetHashLambda(Func<K, long> hashLambda)
        //{
        //    _hashLambda = hashLambda;
        //}

        /// <summary>
        /// Each hash-index needs a function for checking equality,
        /// a user can specify the equality-check function via a call
        /// to this method.
        /// 
        /// This method should be used internally by the index grain and
        /// should not be invoked from other grains.
        /// </summary>
        /// <param name="equalsLambda">equality check function that
        /// should be used for this hash-index</param>
        //void SetEqualsLambda(Func<K, K, bool> equalsLambda)
        //{
        //    _equalsLambda = equalsLambda;
        //}
    }
}
