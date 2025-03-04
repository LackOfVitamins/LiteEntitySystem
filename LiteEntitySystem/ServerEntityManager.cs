using System;
using System.Collections.Generic;
using K4os.Compression.LZ4;
using LiteNetLib;
using LiteEntitySystem.Compatibility;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    internal struct InputBuffer
    {
        public ushort Tick;
        public InputPacketHeader InputHeader;
        public byte[] Data;
    }

    public enum NetPlayerState
    {
        Active,
        WaitingForFirstInput,
        WaitingForFirstInputProcess,
        RequestBaseline
    }

    public enum ServerSendRate : byte
    {
        EqualToFPS = 1,
        HalfOfFPS = 2,
        ThirdOfFPS = 3
    }

    /// <summary>
    /// Server entity manager
    /// </summary>
    public sealed class ServerEntityManager : EntityManager
    {
        private readonly Queue<ushort> _entityIdQueue = new(MaxSyncedEntityCount);
        private readonly Queue<byte> _playerIdQueue = new(MaxPlayers);
        private readonly Queue<RemoteCallPacket> _rpcPool = new();
        private readonly Queue<byte[]> _inputPool = new();
        private readonly Queue<NetDataReader> _pendingClientRequests = new();
        private byte[] _packetBuffer = new byte[(MaxParts+1) * NetConstants.MaxPacketSize];
        private readonly NetPlayer[] _netPlayersArray = new NetPlayer[MaxPlayers];
        private readonly NetPlayer[] _netPlayersDict = new NetPlayer[MaxPlayers+1];
        private readonly StateSerializer[] _stateSerializers = new StateSerializer[MaxSyncedEntityCount];
        public const int MaxStoredInputs = 30;

        private byte[] _compressionBuffer = new byte[4096];
        private int _netPlayersCount;

        /// <summary>
        /// Network players count
        /// </summary>
        public int PlayersCount => _netPlayersCount;
        
        /// <summary>
        /// Rate at which server will make and send packets
        /// </summary>
        public readonly ServerSendRate SendRate;

        private ushort _minimalTick;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="inputProcessor">Input processor (you can use default InputProcessor/<T/> or derive from abstract one to make your own input serialization</param>
        /// <param name="packetHeader">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        /// <param name="sendRate">Send rate of server (depends on fps)</param>
        public ServerEntityManager(
            EntityTypesMap typesMap, 
            InputProcessor inputProcessor,
            byte packetHeader, 
            byte framesPerSecond,
            ServerSendRate sendRate) 
            : base(typesMap, inputProcessor, NetworkMode.Server, framesPerSecond, packetHeader)
        {
            InternalPlayerId = ServerPlayerId;
            for (int i = 1; i <= byte.MaxValue; i++)
                _playerIdQueue.Enqueue((byte)i);
            for (ushort i = FirstEntityId; i < MaxSyncedEntityCount; i++)
                _entityIdQueue.Enqueue(i);

            _packetBuffer[0] = packetHeader;
            SendRate = sendRate;
        }

        /// <summary>
        /// Create and add new player
        /// </summary>
        /// <param name="peer">NetPeer to use</param>
        /// <param name="assignToTag">assign new player to NetPeer.Tag for usability</param>
        /// <returns>Newly created player, null if players count is maximum</returns>
        public NetPlayer AddPlayer(NetPeer peer, bool assignToTag)
        {
            if (_netPlayersCount == MaxPlayers)
                return null;
            var player = new NetPlayer(peer, _playerIdQueue.Dequeue()) { State = NetPlayerState.RequestBaseline };
            _netPlayersDict[player.Id] = player;
            player.ArrayIndex = _netPlayersCount;
            _netPlayersArray[_netPlayersCount++] = player;
            if (assignToTag)
                peer.Tag = player;
            return player;
        }

        /// <summary>
        /// Remove player using NetPeer.Tag (is you assigned it or used <see cref="AddPlayer"/> with assignToTag)
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>true if player removed successfully, false if player not found</returns>
        public bool RemovePlayerFromPeerTag(NetPeer player)
        {
            return RemovePlayer(player.Tag as NetPlayer);
        }

        /// <summary>
        /// Remove player and it's owned entities
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>true if player removed successfully, false if player not found</returns>
        public bool RemovePlayer(NetPlayer player)
        {
            if (player == null || _netPlayersDict[player.Id] == null)
                return false;

            GetPlayerController(player)?.DestroyWithControlledEntity();
            
            _netPlayersDict[player.Id] = null;
            _netPlayersCount--;
            _playerIdQueue.Enqueue(player.Id);
            
            if (player.ArrayIndex != _netPlayersCount)
            {
                _netPlayersArray[player.ArrayIndex] = _netPlayersArray[_netPlayersCount];
                _netPlayersArray[player.ArrayIndex].ArrayIndex = player.ArrayIndex;
            }
            _netPlayersArray[_netPlayersCount] = null;
            return true;
        }

        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        /// <param name="player">player</param>
        /// <returns>Instance if found, null if not</returns>
        public ControllerLogic GetPlayerController(NetPeer player)
        {
            return GetPlayerController(player.Tag as NetPlayer);
        }
        
        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>Instance if found, null if not</returns>
        public ControllerLogic GetPlayerController(NetPlayer player)
        {
            if (player == null || _netPlayersDict[player.Id] == null)
                return null;
            foreach (var controller in GetControllers<ControllerLogic>())
            {
                if (controller.OwnerId == player.Id)
                    return controller;
            }
            return null;
        }

        /// <summary>
        /// Add new player controller entity
        /// </summary>
        /// <param name="owner">Player that owns this controller</param>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddController<T>(NetPlayer owner, Action<T> initMethod = null) where T : ControllerLogic
        {
            var result = Add<T>(ent =>
            {
                ent.InternalOwnerId = owner.Id;
                initMethod?.Invoke(ent);
            });
            return result;
        }
        
        /// <summary>
        /// Add new AI controller entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddAIController<T>(Action<T> initMethod = null) where T : AiControllerLogic
        {
            return Add(initMethod);
        }

        public void RemoveAIController<T>(T controller) where T : AiControllerLogic
        {
            controller.StopControl();
            RemoveEntity(controller);
        }

        /// <summary>
        /// Add new entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddSignleton<T>(Action<T> initMethod = null) where T : SingletonEntityLogic
        {
            return Add(initMethod);
        }

        /// <summary>
        /// Add new entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddEntity<T>(Action<T> initMethod = null) where T : EntityLogic
        {
            return Add(initMethod);
        }
        
        /// <summary>
        /// Add new entity and set parent entity
        /// </summary>
        /// <param name="parent">Parent entity</param>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddEntity<T>(EntityLogic parent, Action<T> initMethod = null) where T : EntityLogic
        {
            var entity = Add(initMethod);
            entity.SetParent(parent);
            return entity;
        }
        
        /// <summary>
        /// Read data from NetPeer with assigned NetPlayer to NetPeer.Tag
        /// </summary>
        /// <param name="peer">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        public void Deserialize(NetPeer peer, NetDataReader reader)
        {
            Deserialize((NetPlayer)peer.Tag, reader);
        }

        /// <summary>
        /// Read incoming data in case of first byte is == headerByte
        /// </summary>
        /// <param name="player">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPlayer player, NetDataReader reader)
        {
            if (reader.PeekByte() == HeaderByte)
            {
                reader.SkipBytes(1);
                Deserialize(player, reader);
                return true;
            }

            return false;
        }
        
        /// <summary>
        /// Read incoming data in case of first byte is == headerByte from NetPeer with assigned NetPlayer to NetPeer.Tag
        /// </summary>
        /// <param name="peer">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPeer peer, NetDataReader reader)
        {
            if (reader.PeekByte() == HeaderByte)
            {
                reader.SkipBytes(1);
                Deserialize((NetPlayer)peer.Tag, reader);
                return true;
            }

            return false;
        }
        
        private readonly byte[] _inputDecodeBuffer = new byte[NetConstants.MaxUnreliableDataSize];
        
        /// <summary>
        /// Read data from NetPlayer
        /// </summary>
        /// <param name="player">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        public unsafe void Deserialize(NetPlayer player, NetDataReader reader)
        {
            if (reader.AvailableBytes < 3)
            {
                Logger.LogWarning($"Invalid data received: {reader.AvailableBytes}");
                return;
            }
            byte packetType = reader.GetByte();
            if (packetType == InternalPackets.ClientRequest)
            {
                _pendingClientRequests.Enqueue(new NetDataReader(reader.GetRemainingBytes()));
                return;
            }
            if (packetType != InternalPackets.ClientInput)
            {
                Logger.LogWarning($"[SEM] Unknown packet type: {packetType}");
                return;
            }
            ushort clientTick = reader.GetUShort();
            bool isFirstInput = true;
            while (reader.AvailableBytes >= sizeof(InputPacketHeader))
            {
                var inputBuffer = new InputBuffer{ Tick = clientTick };
                fixed (byte* rawData = reader.RawData)
                    inputBuffer.InputHeader = *(InputPacketHeader*)(rawData + reader.Position);
                reader.SkipBytes(sizeof(InputPacketHeader));
                
                //possibly empty but with header
                if (isFirstInput && reader.AvailableBytes < InputProcessor.InputSize)
                {
                    Logger.LogError($"Bad input from: {player.Id} - {player.Peer.EndPoint} too small input");
                    return;
                }
                if (!isFirstInput && reader.AvailableBytes < InputProcessor.MinDeltaSize)
                {
                    Logger.LogError($"Bad input from: {player.Id} - {player.Peer.EndPoint} too small delta");
                    return;
                }
                if (Utils.SequenceDiff(inputBuffer.InputHeader.StateA, Tick) > 0 ||
                    Utils.SequenceDiff(inputBuffer.InputHeader.StateB, Tick) > 0)
                {
                    Logger.LogError($"Bad input from: {player.Id} - {player.Peer.EndPoint} invalid sequence");
                    return;
                }
                inputBuffer.InputHeader.LerpMsec = MathUtils.Clamp(inputBuffer.InputHeader.LerpMsec, 0f, 1f);
                
                //decode delta
                Span<byte> decodedData;
                if (!isFirstInput) //delta
                {
                    Array.Clear(_inputDecodeBuffer, 0, InputProcessor.InputSize);
                    decodedData = new Span<byte>(_inputDecodeBuffer, 0, InputProcessor.InputSize);
                    int readBytes = InputProcessor.DeltaDecode(new ReadOnlySpan<byte>(reader.RawData, reader.Position, reader.AvailableBytes), decodedData);
                    reader.SkipBytes(readBytes);
                }
                else //full
                {
                    isFirstInput = false;
                    decodedData = new Span<byte>(reader.RawData, reader.Position, InputProcessor.InputSize);
                    InputProcessor.DeltaDecodeInit(decodedData);
                    reader.SkipBytes(InputProcessor.InputSize);
                }
                //Logger.Log($"ReadInput: {clientTick} stateA: {inputBuffer.InputHeader.StateA}");
                clientTick++;
                
                if (Utils.SequenceDiff(inputBuffer.InputHeader.StateB, player.CurrentServerTick) > 0)
                    player.CurrentServerTick = inputBuffer.InputHeader.StateB;
                    
                //read input
                if (player.State == NetPlayerState.WaitingForFirstInput || Utils.SequenceDiff(inputBuffer.Tick, player.LastReceivedTick) > 0)
                {
                    _inputPool.TryDequeue(out inputBuffer.Data);
                    Utils.ResizeOrCreate(ref inputBuffer.Data, InputProcessor.InputSize);
                    fixed(byte* inputData = inputBuffer.Data, rawDecodedData = decodedData)
                        RefMagic.CopyBlock(inputData, rawDecodedData, (uint)InputProcessor.InputSize);

                    if (player.AvailableInput.Count == MaxStoredInputs)
                        _inputPool.Enqueue(player.AvailableInput.ExtractMin().Data);
                    player.AvailableInput.Add(inputBuffer, inputBuffer.Tick);

                    //to reduce data
                    player.LastReceivedTick = inputBuffer.Tick;
                }
            }
            if(player.State == NetPlayerState.WaitingForFirstInput)
                player.State = NetPlayerState.WaitingForFirstInputProcess;
        }

        public override unsafe void Update()
        {
            ushort prevTick = _tick;
            base.Update();
            
            //send only if tick changed
            if (_netPlayersCount == 0 || prevTick == _tick || _tick % (int) SendRate != 0)
                return;

            //calculate minimalTick and potential baseline size
            ushort executedTick = (ushort)(_tick - 1);
            _minimalTick = executedTick;
            
            int maxBaseline = 0;
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (player.State != NetPlayerState.RequestBaseline)
                    _minimalTick = Utils.SequenceDiff(player.StateATick, _minimalTick) < 0 ? player.StateATick : _minimalTick;
                else if (maxBaseline == 0)
                {
                    maxBaseline = sizeof(BaselineDataHeader);
                    for (ushort i = FirstEntityId; i <= MaxSyncedEntityId; i++)
                        maxBaseline += _stateSerializers[i].GetMaximumSize(executedTick);
                    if (_packetBuffer.Length < maxBaseline)
                        _packetBuffer = new byte[maxBaseline];
                    int maxCompressedSize = LZ4Codec.MaximumOutputSize(_packetBuffer.Length) + sizeof(BaselineDataHeader);
                    if (_compressionBuffer.Length < maxCompressedSize)
                        _compressionBuffer = new byte[maxCompressedSize];
                }
            }

            //make packets
            fixed (byte* packetBuffer = _packetBuffer, compressionBuffer = _compressionBuffer)
            // ReSharper disable once BadChildStatementIndent
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (player.State == NetPlayerState.RequestBaseline)
                {
                    int originalLength = 0;
                    for (ushort i = FirstEntityId; i <= MaxSyncedEntityId; i++)
                        _stateSerializers[i].MakeBaseline(player.Id, executedTick, _minimalTick, packetBuffer, ref originalLength);
                    
                    //set header
                    *(BaselineDataHeader*)compressionBuffer = new BaselineDataHeader
                    {
                        UserHeader = HeaderByte,
                        PacketType = InternalPackets.BaselineSync,
                        OriginalLength = originalLength,
                        Tick = executedTick,
                        PlayerId = player.Id,
                        SendRate = (byte)SendRate
                    };
                    
                    //compress
                    int encodedLength = LZ4Codec.Encode(
                        packetBuffer,
                        originalLength,
                        compressionBuffer + sizeof(BaselineDataHeader),
                        _compressionBuffer.Length - sizeof(BaselineDataHeader),
                        LZ4Level.L00_FAST);
                    
                    player.Peer.Send(_compressionBuffer, 0, sizeof(BaselineDataHeader) + encodedLength, DeliveryMethod.ReliableOrdered);
                    player.StateATick = executedTick;
                    player.CurrentServerTick = executedTick;
                    player.State = NetPlayerState.WaitingForFirstInput;
                    Logger.Log($"[SEM] SendWorld to player {player.Id}. orig: {originalLength}, bytes, compressed: {encodedLength}, ExecutedTick: {executedTick}");
                    continue;
                }
                if (player.State != NetPlayerState.Active)
                {
                    //waiting to load initial state
                    continue;
                }
                
                //Partial diff sync
                var header = (DiffPartHeader*)packetBuffer;
                header->UserHeader = HeaderByte;
                header->Part = 0;
                header->Tick = executedTick;
                int writePosition = sizeof(DiffPartHeader);
                
                ushort maxPartSize = (ushort)(player.Peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) - sizeof(LastPartData));
                for (ushort eId = FirstEntityId; eId <= MaxSyncedEntityId; eId++)
                {
                    var diffResult = _stateSerializers[eId].MakeDiff(
                        player.Id,
                        executedTick,
                        _minimalTick,
                        player.CurrentServerTick,
                        packetBuffer,
                        ref writePosition);
                    if (diffResult == DiffResult.DoneAndDestroy)
                    {
                        _entityIdQueue.Enqueue(eId);
                    }
                    else if (diffResult == DiffResult.Done)
                    {
                        int overflow = writePosition - maxPartSize;
                        while (overflow > 0)
                        {
                            if (header->Part == MaxParts-1)
                            {
                                Logger.Log($"P:{pidx} Request baseline {executedTick}");
                                player.State = NetPlayerState.RequestBaseline;
                                break;
                            }
                            header->PacketType = InternalPackets.DiffSync;
                            //Logger.LogWarning($"P:{pidx} Sending diff part {*partCount}: {_tick}");
                            player.Peer.Send(_packetBuffer, 0, maxPartSize, DeliveryMethod.Unreliable);
                            header->Part++;

                            //repeat in next packet
                            RefMagic.CopyBlock(packetBuffer + sizeof(DiffPartHeader), packetBuffer + maxPartSize, (uint)overflow);
                            writePosition = sizeof(DiffPartHeader) + overflow;
                            overflow = writePosition - maxPartSize;
                        }
                        //if request baseline break entity loop
                        if(player.State == NetPlayerState.RequestBaseline)
                            break;
                    }
                    //else skip
                }
                
                //if request baseline continue to other players
                if(player.State == NetPlayerState.RequestBaseline)
                    continue;

                //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                header->PacketType = InternalPackets.DiffSyncLast;
                //put mtu at last packet
                *(LastPartData*)(packetBuffer + writePosition) = new LastPartData
                {
                    LastProcessedTick = player.LastProcessedTick,
                    LastReceivedTick = player.LastReceivedTick,
                    Mtu = maxPartSize
                };
                writePosition += sizeof(LastPartData);
                player.Peer.Send(_packetBuffer, 0, writePosition, DeliveryMethod.Unreliable);
            }

            //trigger only when there is data
            _netPlayersArray[0].Peer.NetManager.TriggerUpdate();
        }
        
        private T Add<T>(Action<T> initMethod) where T : InternalEntity
        {
            if (EntityClassInfo<T>.ClassId == 0)
            {
                throw new Exception($"Unregistered entity type: {typeof(T)}");
            }
            //create entity data and filters
            ref var classData = ref ClassDataDict[EntityClassInfo<T>.ClassId];
            T entity;
            
            if (classData.IsLocalOnly)
            {
                entity = AddLocalEntity(initMethod);
            }
            else
            {
                if (_entityIdQueue.Count == 0)
                {
                    Logger.Log($"Cannot add entity. Max entity count reached: {MaxSyncedEntityCount}");
                    return null;
                }
                ushort entityId = _entityIdQueue.Dequeue();
                ref var stateSerializer = ref _stateSerializers[entityId];

                entity = (T)AddEntity(new EntityParams(
                    classData.ClassId, 
                    entityId,
                    stateSerializer.IncrementVersion(_tick),
                    this));
                stateSerializer.Init(ref classData, entity);
                
                initMethod?.Invoke(entity);
                ConstructEntity(entity);
            }
            //Debug.Log($"[SEM] Entity create. clsId: {classData.ClassId}, id: {entityId}, v: {version}");
            return entity;
        }
        
        public NetPlayer GetPlayer(byte ownerId)
        {
            return _netPlayersDict[ownerId];
        }

        protected override void OnLogicTick()
        {
            //read pending client requests
            while (_pendingClientRequests.Count > 0)
                InputProcessor.ReadClientRequest(this, _pendingClientRequests.Dequeue());
            
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (player.State == NetPlayerState.RequestBaseline) 
                    continue;
                if (player.AvailableInput.Count == 0)
                {
                    //Logger.LogWarning($"Inputs of player {pidx} is zero");
                    continue;
                }
                
                var inputFrame = player.AvailableInput.ExtractMin();
                ref var inputData = ref inputFrame.InputHeader;
                player.LastProcessedTick = inputFrame.Tick;
                player.StateATick = inputData.StateA;
                player.StateBTick = inputData.StateB;
                player.LerpTime = inputData.LerpMsec;
                //Logger.Log($"[SEM] CT: {player.LastProcessedTick}, stateA: {player.StateATick}, stateB: {player.StateBTick}");
                player.SimulatedServerTick = Utils.LerpSequence(inputData.StateA, (ushort)(inputData.StateB-1), inputData.LerpMsec);
                if (player.State == NetPlayerState.WaitingForFirstInputProcess)
                    player.State = NetPlayerState.Active;

                //process input
                if (inputFrame.Data != null)
                {
                    InputProcessor.ReadInput(this, player.Id, inputFrame.Data);
                    _inputPool.Enqueue(inputFrame.Data);
                    inputFrame.Data = null;
                }
            }

            foreach (var aliveEntity in AliveEntities)
                aliveEntity.Update();

            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(_tick);
        }

        internal override void RemoveEntity(InternalEntity e)
        {
            base.RemoveEntity(e);
            if (!e.IsLocal)
            {
                _stateSerializers[e.Id].Destroy(_tick, _minimalTick, PlayersCount == 0);
                //destroy instantly when no players to free ids
                if (PlayersCount == 0)
                    _entityIdQueue.Enqueue(e.Id);
            }
        }
        
        internal void PoolRpc(RemoteCallPacket rpcNode)
        {
            _rpcPool.Enqueue(rpcNode);
        }
        
        internal void AddRemoteCall(ushort entityId, ushort rpcId, ExecuteFlags flags)
        {
            if (PlayersCount == 0)
                return;
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, 0, rpcId, flags, 0);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, T value, ushort rpcId, ExecuteFlags flags) where T : unmanaged
        {
            if (PlayersCount == 0)
                return;
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, (ushort)sizeof(T), rpcId, flags, 1);
            fixed (byte* rawData = rpc.Data)
                *(T*)rawData = value;
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, ReadOnlySpan<T> value, ushort rpcId, ExecuteFlags flags) where T : unmanaged
        {
            if (PlayersCount == 0)
                return;
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, (ushort)sizeof(T), rpcId, flags, value.Length);
            fixed(void* rawValue = value, rawData = rpc.Data)
                RefMagic.CopyBlock(rawData, rawValue, (uint)rpc.TotalSize);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
    }
}
