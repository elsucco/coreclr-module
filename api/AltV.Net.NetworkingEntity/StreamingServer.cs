﻿using System.Text;
using System.Threading.Tasks;
using Entity;
using Google.Protobuf;
using WebSocket = net.vieapps.Components.WebSockets.WebSocket;

namespace AltV.Net.NetworkingEntity
{
    //TODO: synchronize events via channel, also the event for transferring all entities to keep order, but for speed we probably need a channel for each player
    //TODO: verify if the synchronization of the entity storage is enough to verify order of (entity delete) / (entity add) and (get all entities) on connect 
    //TODO: add data change events
    //TODO: add position change events
    //TODO: sync entity data only to near players on change ect and only send entity data when entering streaming range
    //TODO: regenerate tokens for both players when two are trying to use same token
    public class StreamingServer
    {
        private readonly WebSocket webSocket;

        private readonly AuthProvider authProvider = new AuthProvider();

        private readonly WebSocketRepository webSocketRepository = new WebSocketRepository();

        private readonly EntityIdStorage entityIdStorage = new EntityIdStorage();

        private readonly EntityRepository entityRepository = new EntityRepository();

        public StreamingServer()
        {
            entityRepository.OnEntityAdd += entity =>
            {
                var createEvent = new Event {Create = {Entity = entity}};
                webSocketRepository.SendToAll(createEvent);
            };
            entityRepository.OnEntityRemove += id =>
            {
                var deleteEvent = new Event {Delete = {Id = id}};
                webSocketRepository.SendToAll(deleteEvent);
            };
            entityRepository.OnEntityPositionUpdate += (id, position) =>
            {
                var deleteEvent = new Event {PositionChange = {Id = id, Position = position}};
                webSocketRepository.SendToAll(deleteEvent);
            };
            entityRepository.OnEntityDataUpdate += (id, key, value) =>
            {
                var deleteEvent = new Event {DataChange = {Id = id, Key = key, Value = value}};
                webSocketRepository.SendToAll(deleteEvent);
            };
            
            Alt.OnPlayerConnect += (player, reason) =>
            {
                Task.Run(() => { authProvider.SendAuthentication(player); });
            };
            Alt.OnPlayerRemove += player =>
            {
                Task.Run(async () =>
                {
                    var task = webSocketRepository.Remove(player, webSocket);
                    if (task != null)
                    {
                        await task;
                    }

                    authProvider.RemoveAuthentication(player);
                });
            };
            webSocket = new WebSocket
            {
                OnError = (webSocket, exception) =>
                {
                    // your code to handle error
                },
                OnConnectionEstablished = (webSocket) =>
                {
                    // your code to handle established connection
                },
                OnConnectionBroken = (webSocket) =>
                {
                    // your code to handle broken connection
                },
                OnMessageReceived = (webSocket, result, data) =>
                {
                    Task.Run(() =>
                    {
                        var token = Encoding.UTF8.GetString(data, 0, data.Length);
                        var player = authProvider.VerifyAuthentication(token);
                        lock (player)
                        {
                            if (player.Exists)
                            {
                                webSocketRepository.Add(player, webSocket);
                            }

                            //players.Remove(token); //TODO: keep token alive so we can reconnect on connection lost
                        }

                        var sendEvent = new Event();
                        var currSendEvent = new EntitySendEvent();
                        lock (entityRepository.Entities)
                        {
                            currSendEvent.Entities.Add(entityRepository.GetAll());
                            sendEvent.Send = currSendEvent;
                            webSocket.SendAsync(sendEvent.ToByteArray(), false);
                        }
                    });
                }
            };
            webSocket.StartListen();
        }

        public void CreateEntity(Entity.Entity entity)
        {
            var id = entityIdStorage.GetNext();
            entity.Id = id;
            entityRepository.Add(entity);
        }

        public void DeleteEntity(ulong id)
        {
            entityIdStorage.Free(id);
            entityRepository.Delete(id);
        }

        public void UpdateEntityPosition(ulong id, Position position)
        {
            entityRepository.UpdatePosition(id, position);
        }

        public void UpdateEntityData(ulong id, string key, MValue value)
        {
            entityRepository.UpdateData(id, key, value);
        }
    }
}