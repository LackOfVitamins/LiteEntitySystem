using System;
using System.Collections.Generic;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    /// <summary>
    /// Entity has update method
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class UpdateableEntity : Attribute
    {
        public readonly bool UpdateOnClient;

        public UpdateableEntity() { }
        
        public UpdateableEntity(bool updateOnClient)
        {
            UpdateOnClient = updateOnClient;
        }
    }

    /// <summary>
    /// Entity is local only (only on server or client no difference)
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class LocalOnly : Attribute { }
    
    [AttributeUsage(AttributeTargets.Method)]
    public class RemoteCall : Attribute
    {
        public readonly ExecuteFlags Flags;
        
        internal byte Id = byte.MaxValue;
        internal int DataSize;
        internal bool IsArray;

        public RemoteCall(ExecuteFlags flags)
        {
            Flags = flags;
        }
    }

    /// <summary>
    /// Base class for simple (not controlled by controller) entity
    /// </summary>
    public abstract class EntityLogic : InternalEntity
    {
        [SyncVar(nameof(OnParentChange))] 
        private ushort _parentId = EntityManager.InvalidEntityId;
        
        [SyncVar(nameof(OnDestroyChange))] 
        private bool _isDestroyed;
        
        [SyncVar(nameof(OnOwnerChange))]
        internal byte InternalOwnerId;
        
        /// <summary>
        /// Is entity is destroyed
        /// </summary>
        public bool IsDestroyed => _isDestroyed;

        /// <summary>
        /// Child entities (can be used for transforms or as components)
        /// </summary>
        public readonly HashSet<EntityLogic> Childs = new HashSet<EntityLogic>();

        /// <summary>
        /// Owner player id
        /// </summary>
        public byte OwnerId => InternalOwnerId;
        
        /// <summary>
        /// Destroy entity
        /// </summary>
        public void Destroy()
        {
            if (EntityManager.IsClient || _isDestroyed)
                return;
            DestroyInternal();
        }
        
        /// <summary>
        /// Create predicted entity (like projectile) that will be replaced by server entity if prediction is successful
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <param name="initMethod">Method that will be called after entity constructed</param>
        /// <returns>Created predicted local entity</returns>
        public T AddPredictedEntity<T>(Action<T> initMethod = null) where T : EntityLogic
        {
            if (EntityManager.IsServer)
            {
                if (InternalOwnerId == ServerEntityManager.ServerPlayerId)
                {
                    return ServerManager.AddEntity(initMethod);
                }

                var predictedEntity = ServerManager.AddEntity(initMethod);
                var player = ServerManager.GetPlayer(InternalOwnerId);
                ushort playerServerTick = player.SimulatedServerTick;
                while (playerServerTick != ServerManager.Tick)
                {
                    predictedEntity.Update();
                    playerServerTick++;
                }

                return predictedEntity;
            }
            
            var entity = EntityManager.AddLocalEntity(initMethod);
            ClientManager.AddPredictedInfo(entity);
            return entity;
        }

        /// <summary>
        /// Set parent entity
        /// </summary>
        /// <param name="parentEntity">parent entity</param>
        public void SetParent(EntityLogic parentEntity)
        {
            if (EntityManager.IsClient)
                return;
            
            ushort id = parentEntity?.Id ?? EntityManager.InvalidEntityId;
            if (id == _parentId)
                return;
            
            ushort oldId = _parentId;
            _parentId = id;
            OnParentChange(oldId);
            
            var newParent = EntityManager.GetEntityById<EntityLogic>(_parentId);
            InternalOwnerId = newParent?.InternalOwnerId ?? 0;
            if (InternalOwnerId != oldId)
            {
                SetOwner(this, InternalOwnerId);
            }
        }
        
        /// <summary>
        /// Get parent entity
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <returns>parent entity</returns>
        public T GetParent<T>() where T : EntityLogic
        {
            return EntityManager.GetEntityByIdSafe<T>(_parentId);
        }
        
        /// <summary>
        /// Called when lag compensation was started for this entity
        /// </summary>
        public virtual void OnLagCompensationStart()
        {
            
        }
        
        /// <summary>
        /// Called when lag compensation ended for this entity
        /// </summary>
        public virtual void OnLagCompensationEnd()
        {
            
        }
        
        internal override bool IsControlledBy(byte playerId)
        {
            return playerId == InternalOwnerId;
        }

        internal void DestroyInternal()
        {
            _isDestroyed = true;
            OnDestroy();
            EntityManager.RemoveEntity(this);
            if (EntityManager.IsClient && IsLocalControlled && !IsLocal)
            {
                ClientManager.RemoveOwned(this);
            }
            else if (EntityManager.IsServer)
            {
                foreach (var e in Childs)
                    e.DestroyInternal();
                ServerManager.DestroySavedData(this);
            }

            var parent = EntityManager.GetEntityByIdSafe<EntityLogic>(_parentId);
            if (parent != null && !parent._isDestroyed)
            {
                parent.Childs.Remove(this);
            }
        }
        
        private void OnOwnerChange(byte prevOwner)
        {
            if(IsLocalControlled && !IsLocal)
                ClientManager.AddOwned(this);
            else if(prevOwner == EntityManager.InternalPlayerId && !IsLocal)
                ClientManager.RemoveOwned(this);
        }

        private void OnDestroyChange(bool prevValue)
        {
            if (_isDestroyed)
                DestroyInternal();
        }

        private void OnParentChange(ushort oldId)
        {
            EntityManager.GetEntityByIdSafe<EntityLogic>(oldId)?.Childs.Remove(this);
            EntityManager.GetEntityByIdSafe<EntityLogic>(_parentId)?.Childs.Add(this);
        }

        internal static void SetOwner(EntityLogic entity, byte ownerId)
        {
            foreach (var child in entity.Childs)
            {
                child.InternalOwnerId = ownerId;
                SetOwner(child, ownerId);
            }
        }

        protected virtual void OnDestroy()
        {

        }

        protected EntityLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for singletons entity that can exists in only one instance
    /// </summary>
    public abstract class SingletonEntityLogic : InternalEntity
    {
        internal override bool IsControlledBy(byte playerId)
        {
            return false;
        }

        protected SingletonEntityLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for entites that can be controlled by Controller
    /// </summary>
    [UpdateableEntity]
    public abstract class PawnLogic : EntityLogic
    {
        [SyncVar] 
        private ControllerLogic _controller;

        public ControllerLogic Controller
        {
            get => _controller;
            internal set
            {
                InternalOwnerId = value?.InternalOwnerId ?? (GetParent<EntityLogic>()?.InternalOwnerId ?? ServerEntityManager.ServerPlayerId);
                SetOwner(this, InternalOwnerId);
                _controller = value;
            }
        }
        
        protected void EnableLagCompensation()
        {
            if (EntityManager.IsServer)
                ((ServerEntityManager)EntityManager).EnableLagCompensation(this);
        }

        protected void DisableLagCompensation()
        {
            if (EntityManager.IsServer)
                ((ServerEntityManager)EntityManager).DisableLagCompensation();
        }

        public override void Update()
        {
            _controller?.BeforeControlledUpdate();
        }

        protected override void OnDestroy()
        {
            _controller?.OnControlledDestroy();
        }

        protected PawnLogic(EntityParams entityParams) : base(entityParams) { }
    }
    
    /// <summary>
    /// Base class for Controller entities
    /// </summary>
    public abstract class ControllerLogic : InternalEntity
    {
        [SyncVar] 
        internal byte InternalOwnerId;
        
        [SyncVar] 
        private PawnLogic _controlledEntity;

        public byte OwnerId => InternalOwnerId;
        public PawnLogic ControlledEntity => _controlledEntity;

        internal override bool IsControlledBy(byte playerId)
        {
            return InternalOwnerId == playerId;
        }
        
        public virtual void BeforeControlledUpdate()
        {
            
        }

        public void StartControl<T>(T target) where T : PawnLogic
        {
            StopControl();
            _controlledEntity = target;
            _controlledEntity.Controller = this;
        }

        internal void OnControlledDestroy()
        {
            StopControl();
        }

        public void StopControl()
        {
            if (_controlledEntity == null)
                return;
            _controlledEntity.Controller = null;
            _controlledEntity = null;
        }
        
        protected ControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for AI Controller entities
    /// </summary>
    [LocalOnly, UpdateableEntity]
    public abstract class AiControllerLogic : ControllerLogic
    {
        protected AiControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for AI Controller entities with typed ControlledEntity field
    /// </summary>
    [LocalOnly, UpdateableEntity]
    public abstract class AiControllerLogic<T> : AiControllerLogic where T : PawnLogic
    {
        public new T ControlledEntity => base.ControlledEntity as T;
        
        protected AiControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for human Controller entities
    /// </summary>
    [UpdateableEntity(true)]
    public abstract class HumanControllerLogic : ControllerLogic
    {
        [SyncVar(nameof(OnDestroyChange))] 
        private bool _isDestroyed;
        
        /// <summary>
        /// Called on client and server to read generated from <see cref="GenerateInput"/> input
        /// </summary>
        /// <param name="reader"></param>
        public abstract void ReadInput(NetDataReader reader);
        
        /// <summary>
        /// Called on client to generate input
        /// </summary>
        /// <param name="writer"></param>
        public abstract void GenerateInput(NetDataWriter writer);

        internal void DestroyInternal()
        {
            _isDestroyed = true;
            EntityManager.RemoveEntity(this);
            ServerManager.DestroySavedData(this);
        }
        
        private void OnDestroyChange(bool prevValue)
        {
            if(_isDestroyed)
                OnDestroy();
        }

        protected virtual void OnDestroy()
        {
            
        }

        protected HumanControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for human Controller entities with typed ControlledEntity field
    /// </summary>
    public abstract class HumanControllerLogic<T> : HumanControllerLogic where T : PawnLogic
    {
        public new T ControlledEntity => base.ControlledEntity as T;
        
        protected HumanControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }
}