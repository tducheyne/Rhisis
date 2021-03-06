﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rhisis.Database;
using Rhisis.Database.Entities;
using Rhisis.Network.Packets;
using Rhisis.Network.Packets.World;
using Rhisis.World.Client;
using Rhisis.World.Game.Factories;
using Rhisis.World.Game.Maps;
using Rhisis.World.Game.Maps.Regions;
using Rhisis.World.Packets;
using Rhisis.World.Systems.Death;
using Rhisis.World.Systems.Teleport;
using Sylver.HandlerInvoker.Attributes;
using System;
using System.Linq;

namespace Rhisis.World.Handlers
{
    [Handler]
    public class JoinGameHandler
    {
        private readonly ILogger<JoinGameHandler> _logger;
        private readonly IRhisisDatabase _database;
        private readonly IMapManager _mapManager;
        private readonly IDeathSystem _deathSystem;
        private readonly ITeleportSystem _teleportSystem;
        private readonly IPlayerFactory _playerFactory;
        private readonly IWorldSpawnPacketFactory _worldSpawnPacketFactory;

        /// <summary>
        /// Creates a new <see cref="JoinGameHandler"/> instance.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="database">Database access layer.</param>
        /// <param name="playerFactory">Player factory.</param>
        /// <param name="worldSpawnPacketFactory">World spawn packet factory.</param>
        public JoinGameHandler(ILogger<JoinGameHandler> logger, IRhisisDatabase database, IMapManager mapManager, IDeathSystem deathSystem, ITeleportSystem teleportSystem, IPlayerFactory playerFactory, IWorldSpawnPacketFactory worldSpawnPacketFactory)
        {
            _logger = logger;
            _database = database;
            _mapManager = mapManager;
            _deathSystem = deathSystem;
            _teleportSystem = teleportSystem;
            _playerFactory = playerFactory;
            _worldSpawnPacketFactory = worldSpawnPacketFactory;
        }

        /// <summary>
        /// Prepares the player to join the world.
        /// </summary>
        /// <param name="serverClient">Client.</param>
        /// <param name="packet">Incoming join packet.</param>
        [HandlerAction(PacketType.JOIN)]
        public void OnJoin(IWorldServerClient serverClient, JoinPacket packet)
        {
            DbCharacter character = _database.Characters.Include(x => x.User).FirstOrDefault(x => x.Id == packet.PlayerId);

            if (character == null)
            {
                _logger.LogError($"Invalid player id received from client; cannot find player with id: {packet.PlayerId}");
                return;
            }

            if (character.IsDeleted)
            {
                _logger.LogWarning($"Cannot connect with character '{character.Name}' for user '{character.User.Username}'. Reason: character is deleted.");
                return;
            }

            if (character.User.Authority <= 0)
            {
                _logger.LogWarning($"Cannot connect with '{character.Name}'. Reason: User {character.User.Username} is banned.");
                // TODO: send error to client
                return;
            }

            serverClient.Player = _playerFactory.CreatePlayer(character);
            serverClient.Player.Connection = serverClient;

            if (serverClient.Player.IsDead)
            {
                IMapRevivalRegion revivalRegion = _deathSystem.GetNearestRevivalRegion(serverClient.Player);

                if (revivalRegion == null)
                {
                    _logger.LogError($"Cannot resurect player '{serverClient.Player}'; Revival map region not found.");
                    return;
                }

                _deathSystem.ApplyRevivalHealthPenality(serverClient.Player);
                _deathSystem.ApplyDeathPenality(serverClient.Player, sendToPlayer: false);

                IMapInstance map = _mapManager.GetMap(revivalRegion.MapId);

                _teleportSystem.ChangePosition(serverClient.Player, map, revivalRegion.X, null, revivalRegion.Z);
            }

            serverClient.Player.Object.CurrentLayer.AddEntity(serverClient.Player);

            _worldSpawnPacketFactory.SendPlayerSpawn(serverClient.Player);
            serverClient.Player.Object.Spawned = true;
            serverClient.Player.PlayerData.LoggedInAt = DateTime.UtcNow;
        }
    }
}
