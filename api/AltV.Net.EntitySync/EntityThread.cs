using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using AltV.Net.EntitySync.SpatialPartitions;

namespace AltV.Net.EntitySync
{
    /// <summary>
    /// The entity thread checks in a endless loop if entities should be created or removed for all clients.
    /// And it delivers the results via the provided action callbacks.
    /// </summary>
    public class EntityThread
    {
        private bool running = true;

        private readonly Thread thread;

        private readonly IEntityThreadRepository entityThreadRepository;

        private readonly IClientThreadRepository clientThreadRepository;

        private readonly SpatialPartition spatialPartition;

        private readonly int syncRate;

        private readonly LinkedList<IClient> clientsToRemoveFromEntity = new LinkedList<IClient>();
        private readonly LinkedList<IClient> clientsToResetFromEntity = new LinkedList<IClient>();
        
        private readonly LinkedList<string> changedEntityDataKeys = new LinkedList<string>();

        private readonly Action<IClient, IEntity, LinkedList<string>> onEntityCreate;

        private readonly Action<IClient, IEntity> onEntityRemove;

        private readonly Action<IClient, IEntity, LinkedList<string>> onEntityDataChange;

        private readonly Action<IClient, IEntity, Vector3> onEntityPositionChange;
        
        private readonly Action<IClient, IEntity> onEntityClearCache;

        public EntityThread(IEntityThreadRepository entityThreadRepository, IClientThreadRepository clientThreadRepository,
            SpatialPartition spatialPartition, int syncRate,
            Action<IClient, IEntity, LinkedList<string>> onEntityCreate, Action<IClient, IEntity> onEntityRemove,
            Action<IClient, IEntity, LinkedList<string>> onEntityDataChange,
            Action<IClient, IEntity, Vector3> onEntityPositionChange, Action<IClient, IEntity> onEntityClearCache)
        {
            if (spatialPartition == null)
            {
                throw new ArgumentException("spatialPartition should not be null.");
            }

            if (entityThreadRepository == null)
            {
                throw new ArgumentException("entityThreadRepository should not be null.");
            }

            if (onEntityCreate == null)
            {
                throw new ArgumentException("onEntityCreate should not be null.");
            }

            if (onEntityRemove == null)
            {
                throw new ArgumentException("onEntityRemove should not be null.");
            }

            if (onEntityDataChange == null)
            {
                throw new ArgumentException("onEntityDataChange should not be null.");
            }

            if (onEntityPositionChange == null)
            {
                throw new ArgumentException("onEntityPositionChange should not be null.");
            }
            
            if (onEntityClearCache == null)
            {
                throw new ArgumentException("onEntityPositionChange should not be null.");
            }

            thread = new Thread(OnLoop) {IsBackground = true};
            thread.Start();
            this.entityThreadRepository = entityThreadRepository;
            this.clientThreadRepository = clientThreadRepository;
            this.spatialPartition = spatialPartition;
            this.syncRate = syncRate;
            this.onEntityCreate = onEntityCreate;
            this.onEntityRemove = onEntityRemove;
            this.onEntityDataChange = onEntityDataChange;
            this.onEntityPositionChange = onEntityPositionChange;
            this.onEntityClearCache = onEntityClearCache;
        }

        public void OnLoop()
        {
            while (running)
            {
                try
                {
                    var entities = entityThreadRepository.GetAll();

                    var addedEntities = entityThreadRepository.GetAllAdded();
                    
                    var removedEntities = entityThreadRepository.GetAllDeleted();

                    //TODO: when the id provider add / remove doesn't work use the idprovider inside this loop only
                    // We have to remove first, then add, because the added entities may contain the same ids that are removed as well
                    if (removedEntities != null)
                    {
                        foreach (var removedEntity in removedEntities)
                        {
                            spatialPartition.Remove(removedEntity);
                            foreach (var client in removedEntity.GetClients())
                            {
                                onEntityRemove(client, removedEntity);
                            }

                            foreach (var client in removedEntity.DataSnapshot.GetLastClients())
                            {
                                onEntityClearCache(client, removedEntity);
                            }
                        }
                    }

                    if (addedEntities != null)
                    {
                        foreach (var addedEntity in addedEntities)
                        {
                            spatialPartition.Add(addedEntity);
                        }
                    }

                    var clients = clientThreadRepository.GetAll();

                    var clientsToRemove = clientThreadRepository.GetAllDeleted();

                    if (clientsToRemove != null)
                    {
                        foreach (var clientToRemove in clientsToRemove)
                        {
                            clientToRemove.Snapshot.CleanupEntities(clientToRemove);
                        }
                    }

                    foreach (var client in clients)
                    {
                        if (!client.TryGetDimensionAndPosition(out var dimension, out var position))
                        {
                            continue;
                        }

                        //TODO: cache streamed in entities in list, so we don't have to iterate all entities
                        //TODO: maybe add changed entities to a list as well
                        foreach (var foundEntity in spatialPartition.Find(position, dimension))
                        {
                            if (changedEntityDataKeys.Count != 0)
                            {
                                changedEntityDataKeys.Clear();
                            }

                            foundEntity.AddCheck(client);
                            foundEntity.DataSnapshot.CompareWithClient(changedEntityDataKeys, client);

                            if (foundEntity.TryAddClient(client))
                            {
                                onEntityCreate(client, foundEntity, changedEntityDataKeys);
                            }
                            else if (changedEntityDataKeys != null)
                            {
                                onEntityDataChange(client, foundEntity, changedEntityDataKeys);
                            }
                        }
                    }

                    foreach (var entity in entities)
                    {
                        var lastCheckedClients = entity.GetLastCheckedClients();
                        if (lastCheckedClients.Count != 0)
                        {
                            if (clientsToRemoveFromEntity.Count != 0)
                            {
                                clientsToRemoveFromEntity.Clear();
                            }

                            if (clientsToResetFromEntity.Count != 0)
                            {
                                clientsToResetFromEntity.Clear();
                            }

                            foreach (var (client, lastChecked) in lastCheckedClients)
                            {
                                if (lastChecked)
                                {
                                    clientsToResetFromEntity.AddLast(client);
                                }
                                else
                                {
                                    clientsToRemoveFromEntity.AddLast(client);
                                    onEntityRemove(client, entity);
                                }
                            }

                            var currClient = clientsToResetFromEntity.First;

                            while (currClient != null)
                            {
                                entity.RemoveCheck(currClient.Value);
                                currClient = currClient.Next;
                            }
                            
                            currClient = clientsToRemoveFromEntity.First;

                            while (currClient != null)
                            {
                                entity.RemoveClient(currClient.Value);
                                currClient = currClient.Next;
                            }
                        }

                        // Check if position state is new position so we can set the new position to the entity internal position
                        var (hasNewPosition, hasNewRange, hasNewDimension) = entity.TrySetPropertiesComputing(out var newPosition,
                            out var newRange, out var newDimension);

                        if (hasNewPosition)
                        {
                            spatialPartition.UpdateEntityPosition(entity, newPosition);
                            foreach (var entityClient in entity.GetClients())
                            {
                                onEntityPositionChange(entityClient, entity, newPosition);
                            }
                        }

                        if (hasNewRange)
                        {
                            spatialPartition.UpdateEntityRange(entity, newRange);
                        }
                        
                        if (hasNewDimension)
                        {
                            spatialPartition.UpdateEntityDimension(entity, newDimension);
                        }
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception);
                }

                Thread.Sleep(syncRate);
            }
        }

        public void Stop()
        {
            running = false;
            thread.Join();
        }
    }
}