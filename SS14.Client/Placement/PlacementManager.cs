﻿using Lidgren.Network;
using OpenTK;
using OpenTK.Graphics;
using SFML.Graphics;
using SFML.System;
using SS14.Client.GameObjects;
using SS14.Client.Graphics;
using SS14.Client.Interfaces.GameObjects;
using SS14.Shared.Interfaces.Map;
using SS14.Client.Interfaces.Network;
using SS14.Client.Interfaces.Placement;
using SS14.Client.Interfaces.Player;
using SS14.Client.Interfaces.Resource;
using SS14.Client.Interfaces.UserInterface;
using SS14.Shared.Map;
using SS14.Shared;
using SS14.Shared.GameObjects;
using SS14.Shared.Interfaces.GameObjects;
using SS14.Shared.Interfaces.GameObjects.Components;
using SS14.Shared.IoC;
using SS14.Shared.Prototypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SS14.Client.ResourceManagement;
using SS14.Shared.Interfaces.Network;
using SS14.Shared.Interfaces.Physics;
using SS14.Shared.Maths;
using SS14.Shared.Utility;
using Vector2i = SS14.Shared.Maths.Vector2i;

namespace SS14.Client.Placement
{
    public class PlacementManager : IPlacementManager
    {
        [Dependency]
        public readonly ICollisionManager CollisionManager;
        [Dependency]
        public readonly IClientNetManager NetworkManager;
        [Dependency]
        public readonly IPlayerManager PlayerManager;
        [Dependency]
        public readonly IResourceCache ResourceCache;
        private readonly Dictionary<string, Type> _modeDictionary = new Dictionary<string, Type>();

        public Sprite CurrentBaseSprite;
        public string CurrentBaseSpriteKey = "";
        public PlacementMode CurrentMode;
        public PlacementInformation CurrentPermission;
        public EntityPrototype CurrentPrototype;
        public Direction Direction = Direction.South;
        public bool ValidPosition;

        public PlacementManager()
        {
            Type type = typeof(PlacementMode);
            List<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            List<Type> types = assemblies.SelectMany(t => t.GetTypes()).Where(p => type.IsAssignableFrom(p)).ToList();

            _modeDictionary.Clear();
            foreach (Type t in types)
                _modeDictionary.Add(t.Name, t);

            Clear();
        }

        #region IPlacementManager Members

        public bool IsActive { get; private set; }
        public bool Eraser { get; private set; }

        public event EventHandler PlacementCanceled;

        public void HandleNetMessage(NetIncomingMessage msg)
        {
            var messageType = (PlacementManagerMessage)msg.ReadByte();

            switch (messageType)
            {
                case PlacementManagerMessage.StartPlacement:
                    HandleStartPlacement(msg);
                    break;
                case PlacementManagerMessage.CancelPlacement:
                    Clear();
                    break;
                case PlacementManagerMessage.PlacementFailed:
                    //Sad trombone here.
                    break;
            }
        }

        public void Clear()
        {
            CurrentBaseSprite = null;
            CurrentPrototype = null;
            CurrentPermission = null;
            CurrentMode = null;
            if (PlacementCanceled != null && IsActive && !Eraser) PlacementCanceled(this, null);
            IsActive = false;
            Eraser = false;
        }

        public void Rotate()
        {
            switch (Direction)
            {
                case Direction.North:
                    Direction = Direction.East;
                    break;
                case Direction.East:
                    Direction = Direction.South;
                    break;
                case Direction.South:
                    Direction = Direction.West;
                    break;
                case Direction.West:
                    Direction = Direction.North;
                    break;
            }
        }

        public void HandlePlacement()
        {
            if (IsActive && !Eraser)
                RequestPlacement();
        }

        public void HandleDeletion(IEntity entity)
        {
            if (!IsActive || !Eraser) return;

            NetOutgoingMessage message = NetworkManager.CreateMessage();
            message.Write((byte)NetMessages.RequestEntityDeletion);
            message.Write(entity.Uid);
            NetworkManager.ClientSendMessage(message, NetDeliveryMethod.ReliableUnordered);
        }

        public void ToggleEraser()
        {
            if (!Eraser && !IsActive)
            {
                IsActive = true;
                Eraser = true;
            }
            else Clear();
        }

        public void BeginPlacing(PlacementInformation info)
        {
            Clear();

            IoCManager.Resolve<IUserInterfaceManager>().DragInfo.Reset();

            CurrentPermission = info;

            if (!_modeDictionary.Any(pair => pair.Key.Equals(CurrentPermission.PlacementOption)))
            {
                Clear();
                return;
            }

            Type modeType = _modeDictionary.First(pair => pair.Key.Equals(CurrentPermission.PlacementOption)).Value;
            CurrentMode = (PlacementMode)Activator.CreateInstance(modeType, this);

            if (info.IsTile)
                PreparePlacementTile((Tile)info.TileType);
            else
                PreparePlacement(info.EntityType);
        }

        public void Update(ScreenCoordinates mouseScreen)
        {
            if (currentMap == null || CurrentPermission == null || CurrentMode == null) return;

            ValidPosition = CurrentMode.Update(mouseScreen);
        }

        public void Render()
        {
            if (CurrentMode != null)
            {
                CurrentMode.Render();

                if (CurrentPermission != null && CurrentPermission.Range > 0 && CurrentMode.rangerequired)
                {
                    var pos = CluwneLib.WorldToScreen(PlayerManager.ControlledEntity.GetComponent<ITransformComponent>().Position);
                    CluwneLib.drawHollowCircle((int)Math.Floor(pos.X),
                        (int)Math.Floor(pos.Y),
                        CurrentPermission.Range * CluwneLib.TileSize,
                        3f,
                        Color4.White);
                }
            }
        }

        #endregion IPlacementManager Members

        private void HandleStartPlacement(NetIncomingMessage msg)
        {
            CurrentPermission = new PlacementInformation
            {
                Range = msg.ReadInt32(),
                IsTile = msg.ReadBoolean()
            };

            if (CurrentPermission.IsTile) CurrentPermission.TileType = msg.ReadUInt16();
            else CurrentPermission.EntityType = msg.ReadString();
            CurrentPermission.PlacementOption = msg.ReadString();

            BeginPlacing(CurrentPermission);
        }

        private void PreparePlacement(string templateName)
        {
            EntityPrototype prototype =
                IoCManager.Resolve<IPrototypeManager>().Index<EntityPrototype>(templateName);

            ComponentParameter spriteParam = prototype.GetBaseSpriteParamaters().FirstOrDefault();
            //Will break if states not ordered correctly.

            var spriteName = spriteParam == null ? "" : spriteParam.GetValue<string>();
            Sprite sprite = ResourceCache.GetSprite(spriteName);

            CurrentBaseSprite = sprite;
            CurrentBaseSpriteKey = spriteName;
            CurrentPrototype = prototype;

            IsActive = true;
        }

        private void PreparePlacementTile(Tile tileType)
        {
            var tileDefs = IoCManager.Resolve<ITileDefinitionManager>();

            CurrentBaseSprite = ResourceCache.GetSprite("tilebuildoverlay");
            CurrentBaseSpriteKey = "tilebuildoverlay";

            IsActive = true;
        }

        private void RequestPlacement()
        {
            if (CurrentPermission == null) return;
            if (!ValidPosition) return;

            NetOutgoingMessage message = NetworkManager.CreateMessage();

            message.Write((byte)NetMessages.PlacementManagerMessage);
            message.Write((byte)PlacementManagerMessage.RequestPlacement);
            message.Write(CurrentMode.ModeName);

            message.Write(CurrentPermission.IsTile);

            if (CurrentPermission.IsTile) message.Write(CurrentPermission.TileType);
            else message.Write(CurrentPermission.EntityType);

            message.Write(CurrentMode.mouseWorld.X);
            message.Write(CurrentMode.mouseWorld.Y);

            message.Write((byte)Direction);

            NetworkManager.ClientSendMessage(message, NetDeliveryMethod.ReliableUnordered);
        }

        public Sprite GetDirectionalSprite()
        {
            Sprite spriteToUse = CurrentBaseSprite;

            if (CurrentBaseSprite == null) return null;

            string dirName = (CurrentBaseSpriteKey + "_" + Direction).ToLowerInvariant();

            if (ResourceCache.TryGetResource(dirName, out SpriteResource spriteRes))
                spriteToUse = spriteRes.Sprite;

            return spriteToUse;
        }
    }
}
