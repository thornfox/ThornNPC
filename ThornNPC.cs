using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
public sealed class NpcRecord
{
    public NpcRecord(int id, Pos spawn)
    {
        Id = id;
        Spawn = spawn;
    }

    public int Id { get; }
    public Pos Spawn { get; }
}


public sealed class NpcRegistry
{
    private readonly Dictionary<int, NpcRecord> _npcs = new Dictionary<int, NpcRecord>();
    private int _lastId;

    public IReadOnlyList<NpcRecord> All => new List<NpcRecord>(_npcs.Values);

    // IDs start at 1, count up, and are never reused, even after Remove.
    public int Add(Pos spawn)
    {
        _lastId++;
        _npcs[_lastId] = new NpcRecord(_lastId, spawn);
        return _lastId;
    }

    public bool Remove(int id) => _npcs.Remove(id);

    // Forgets all NPCs but keeps the last ID, so IDs are still never reused after a wipe.
    public void Clear() => _npcs.Clear();

    // Raises the ID counter to at least the value saved in the data file.
    public void KeepLastIdAtLeast(int value)
    {
        if (value > _lastId) _lastId = value;
    }

    public NpcRecord Get(int id) => _npcs.TryGetValue(id, out var record) ? record : null;

    // Re-adds a stored NPC with its original ID (used when loading data).
    public void Restore(NpcRecord record)
    {
        _npcs[record.Id] = record;
        if (record.Id > _lastId) _lastId = record.Id;
    }
}


public static class NpcSelection
{
    // NPC IDs within radius of the admin, nearest first.
    public static IReadOnlyList<int> Within(IEnumerable<NpcRecord> npcs, Pos admin, float radius) =>
        npcs.Select(n => new { n.Id, Distance = n.Spawn.HorizontalDistance(admin) })
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .Select(x => x.Id)
            .ToList();
}


public sealed class PenaltyState
{
    private readonly Dictionary<int, DateTime> _endsAt = new Dictionary<int, DateTime>();

    // Starts a penalty only if none is active. A second hit never extends it.
    public bool TryStart(int id, DateTime now, TimeSpan duration)
    {
        if (IsActive(id, now)) return false;
        _endsAt[id] = now + duration;
        return true;
    }

    public bool IsActive(int id, DateTime now) =>
        _endsAt.TryGetValue(id, out var end) && now < end;

    public DateTime? EndsAt(int id) =>
        _endsAt.TryGetValue(id, out var end) ? end : (DateTime?)null;

    public void Clear(int id) => _endsAt.Remove(id);

    public void ClearAll() => _endsAt.Clear();
}


public struct Pos : IEquatable<Pos>
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public Pos(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    // Horizontal distance (X and Z), used for the admin radius.
    public float HorizontalDistance(Pos other)
    {
        var dx = X - other.X;
        var dz = Z - other.Z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    public bool Equals(Pos other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object obj) => obj is Pos other && Equals(other);

    public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ (Y.GetHashCode() * 17) ^ Z.GetHashCode());

    public override string ToString() => $"({X}, {Y}, {Z})";
}


public sealed class ThornConfig
{
    public const float DefaultRadius = 10f;
    public static readonly TimeSpan DefaultPenalty = TimeSpan.FromSeconds(600);
    public const string DefaultNoteText = "Du hast einen NPC angegriffen. Der NPC kommt später zurück.";

    public ThornConfig()
    {
        PenaltyDefault = DefaultPenalty;
        GuiRadius = DefaultRadius;
        NoteTextDefault = DefaultNoteText;
    }

    public TimeSpan PenaltyDefault { get; set; }
    public float GuiRadius { get; set; }
    public string NoteTextDefault { get; set; }

    // Penalty must be between 10 s and 24 h, otherwise the default is used.
    public static TimeSpan ClampPenalty(TimeSpan value) =>
        value < TimeSpan.FromSeconds(10) || value > TimeSpan.FromSeconds(86400) ? DefaultPenalty : value;

    public static float ClampRadius(float value) => value <= 0f ? DefaultRadius : value;
}

// Movement and sitting for one ThornNPC player. Attached next to ThornNpcBehaviour.
// Patrol: walks the route in a loop, straight-line segments between points computed by ThornPath.
// Sitting: at the end of each loop, sits on a free BaseChair within 15 m (if allowed), stands before the next loop.
public class ThornLocomotion : MonoBehaviour
{
    public const float WalkSpeed = 2.5f;          // metres per second
    public const float PauseAtPoint = 2f;         // seconds at each route point
    public const float SitSeconds = 20f;          // seconds seated at the end of a loop
    public const float ChairSearchRadius = 15f;   // metres

    // Ground height as Convoy does it: ray from far above straight down onto the surface layers.
    // Keeps NPCs on the surface and out of the terrain.
    private static readonly int GroundMask = LayerMask.GetMask("Terrain", "World");

    public static float GroundY(Vector3 position, float fallback)
    {
        // Ray from far above onto the surface layers (as Convoy does). Monument roofs count as ground.
        RaycastHit hit;
        var from = new Vector3(position.x, position.y + 50f, position.z);
        if (Physics.Raycast(from, Vector3.down, out hit, 200f, GroundMask)) return hit.point.y;
        return TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(position) : fallback;
    }

    // Chairs already taken by any ThornNPC (position based, as HumanNPC does).
    private static readonly HashSet<Vector3> TakenChairs = new HashSet<Vector3>();

    private BasePlayer _player;
    private List<Vector3> _route = new List<Vector3>();
    private int _index;
    private Vector3 _start;
    private Vector3 _end;
    private float _segTime;
    private float _segDur;
    private float _pause;
    private bool _allowSit = true;
    private bool _paused;
    private BaseMountable _chair;

    public bool Sitting => _chair != null;

    public void Setup(BasePlayer player, bool allowSit)
    {
        _player = player;
        _allowSit = allowSit;
        KeepStanding();
    }

    // Standing and grounded, not crouched (otherwise the NPC walks in a crouch).
    private void KeepStanding()
    {
        if (_player == null || _player.modelState == null) return;
        _player.modelState.ducked = false;
        _player.modelState.onground = true;
        _player.modelState.sprinting = false;
    }

    // Turns the NPC towards the current segment, so it walks forwards, not sideways.
    // Same way as PathFinding: transform rotation plus view angles, then a network update.
    private void FaceDirection(Vector3 from, Vector3 to)
    {
        var dir = to - from;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;
        var rot = Quaternion.LookRotation(dir);
        transform.rotation = rot;
        _player.viewAngles = rot.eulerAngles;
        _player.SendNetworkUpdate();
    }

    public void SetRoute(List<Vector3> route)
    {
        _route = route ?? new List<Vector3>();
        _index = 0;
        _segDur = 0f;
        _pause = 0f;
    }

    public void SetAllowSit(bool allow) => _allowSit = allow;

    // Paused while a player talks to the NPC (E). Resume continues the same walk.
    public bool Paused => _paused;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    private void FixedUpdate()
    {
        if (_player == null || _player.IsDead() || _route.Count == 0 || _paused) return;

        if (_pause > 0f)
        {
            _pause -= Time.fixedDeltaTime;
            if (_pause > 0f) return;
            Stand(); // leaves the chair when the seated time is over (no-op if not sitting)
        }

        if (_segDur <= 0f)
        {
            if (_index >= _route.Count)
            {
                _index = 0;
                if (TrySit())
                {
                    _pause = SitSeconds;
                    return;
                }
                _pause = PauseAtPoint;
                return;
            }

            _start = _player.transform.position;
            _end = _route[_index];
            _index++;
            var dist = Vector3.Distance(_start, _end);
            if (dist < 0.05f)
            {
                _pause = _index >= _route.Count ? PauseAtPoint : 0f;
                return;
            }
            _segDur = dist / WalkSpeed;
            _segTime = 0f;
            FaceDirection(_start, _end);
        }

        KeepStanding();
        _segTime += Time.fixedDeltaTime;
        var t = Mathf.Clamp01(_segTime / _segDur);
        var next = Vector3.Lerp(_start, _end, t);
        // Snap to the surface every step, so the NPC never walks under the map (as Convoy does).
        next.y = GroundY(next, next.y);
        _player.MovePosition(next);
        // Batched update, not Immediate: an immediate update every physics tick caused the stutter.
        _player.SendNetworkUpdate();

        if (t >= 1f)
        {
            _segDur = 0f;
            // Pause only at the end of a loop. Intermediate points are walked through without stopping.
            _pause = _index >= _route.Count ? PauseAtPoint : 0f;
        }
    }

    private bool TrySit()
    {
        if (!_allowSit || _player == null) return false;

        var found = new List<BaseMountable>();
        Vis.Entities<BaseMountable>(_player.transform.position, ChairSearchRadius, found);
        foreach (var mountable in found.Distinct())
        {
            if (!(mountable is BaseChair)) continue;
            if (mountable.AnyMounted() || mountable.mountAnchor == null) continue;
            var key = mountable.transform.position;
            if (TakenChairs.Contains(key)) continue;

            mountable.MountPlayer(_player);
            _player.OverrideViewAngles(mountable.mountAnchor.transform.rotation.eulerAngles);
            _player.MovePosition(_player.transform.position);
            _player.SendNetworkUpdateImmediate();

            TakenChairs.Add(key);
            _chair = mountable;
            return true;
        }
        return false;
    }

    // Called before the next loop starts and when the admin or a player uses the NPC.
    public void Stand()
    {
        if (_chair == null) return;
        var mounted = _player != null ? _player.GetMounted() : null;
        if (mounted != null)
        {
            mounted.DismountPlayer(_player);
            mounted.SetFlagLocal(BaseEntity.Flags.Busy, false, false);
        }
        TakenChairs.Remove(_chair.transform.position);
        _chair = null;
    }
}


// Ported from PathFinding 1.1.6 by Reneb / Nogrod (Oxide plugin, Rust). Logic kept; classes renamed
// (ThornPath, ThornPathNode, ThornPriorityQueue) so they do not collide with other plugins or .NET types.
public static class ThornPath
{
    private static readonly sbyte[,] Direction = { { 0, -1 }, { 1, 0 }, { 0, 1 }, { -1, 0 }, { 1, -1 }, { 1, 1 }, { -1, 1 }, { -1, -1 } };
    private static readonly Vector3 EyesPosition = new Vector3(0f, 1.6f, 0f);
    private static readonly Vector3 JumpPosition = new Vector3(0f, 1f, 0f);
    private static readonly Vector3 WaterProbe = new Vector3(0f, 0.65f, 0f);
    private static readonly Vector3 KneeHeight = new Vector3(0f, 0.5f, 0f);

    // Water at this position (same test HumanNPC uses for swimming). Water is never walkable.
    public static bool IsWater(Vector3 position) => WaterLevel.Test(position + WaterProbe, false, false);

    // Ground that is too steep is not walkable (normal below about 45 degrees).
    // A straight segment is blocked by anything at knee height or at jump height.
    private static bool SegmentBlocked(Vector3 from, Vector3 to) =>
        Physics.Linecast(from + KneeHeight, to + KneeHeight, _blockLayer | _terrainLayer)
        || Physics.Linecast(from + JumpPosition, to + JumpPosition, _blockLayer | _terrainLayer);

    // A roof or floor directly above (inside a building, under a roof) is not walkable.
    public static bool HasRoofAbove(Vector3 position) =>
        Physics.Raycast(position + Up * 0.5f, Up, 4.5f, _constructionLayer);

    public static bool IsWalkableSlope(Vector3 position)
    {
        RaycastHit hit;
        if (!Physics.Raycast(position + Up * 2f, Down, out hit, 4f, _groundLayer | _terrainLayer)) return false;
        return hit.normal.y >= 0.7f;
    }
    private static readonly Vector3 Up = Vector3.up;
    private static readonly Vector3 Down = Vector3.down;
    private const int MaxDepth = 5000;

    private static uint _size;
    private static ThornPathNode[,] _grid;
    private static ThornPriorityQueue _openList;
    private static int _groundLayer;
    private static int _blockLayer;
    private static int _terrainLayer;
    private static int _constructionLayer;

    public static bool IsReady => _grid != null;

    // Allocates the grid. Call after server initialization (World.Size is known then).
    public static void Init()
    {
        _groundLayer = LayerMask.GetMask("Terrain", "World", "Default");
        _constructionLayer = LayerMask.GetMask("Construction", "World");
        _blockLayer = LayerMask.GetMask("World", "Construction", "Tree", "Deployed", "Default");
        _terrainLayer = LayerMask.GetMask("Terrain");
        _size = World.Size;
        _grid = new ThornPathNode[_size, _size];
        _openList = new ThornPriorityQueue(MaxDepth * Direction.GetLength(0));
    }

    public static void Shutdown()
    {
        _grid = null;
        _openList = null;
    }

    // Straight path first, A* as fallback. Same order as PathFinding.Go.
    public static List<Vector3> Go(Vector3 source, Vector3 target)
    {
        if (!IsReady) return null;
        // Rust's NavMesh knows buildings, monument roofs and walls. Use it first.
        var navRoute = NavMeshRoute(source, target);
        if (navRoute != null) return navRoute;
        return FindLinePath(source, target) ?? FindAStar(source, target);
    }

    // Corners of a complete NavMesh path (without the start point). Null if there is none.
    private static List<Vector3> NavMeshRoute(Vector3 source, Vector3 target)
    {
        var path = new UnityEngine.AI.NavMeshPath();
        if (!UnityEngine.AI.NavMesh.CalculatePath(source, target, UnityEngine.AI.NavMesh.AllAreas, path)) return null;
        if (path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) return null;
        var corners = new List<Vector3>(path.corners);
        if (corners.Count > 0) corners.RemoveAt(0);
        if (corners.Count == 0) return null;
        // The NavMesh path can end a few metres before the exact target (nearest mesh point).
        // Add the exact target, so the NPC does not stop short of it.
        if (Vector3.Distance(corners[corners.Count - 1], target) > 0.3f) corners.Add(target);
        // The NavMesh does not know every tree or rock. Each leg is checked like the grid does;
        // if one is blocked, the grid route is used instead.
        var from = source;
        foreach (var corner in corners)
        {
            if (SegmentBlocked(from, corner)) return null;
            from = corner;
        }
        return corners;
    }

    private static List<Vector3> FindAStar(Vector3 sourcePosition, Vector3 targetPosition)
    {
        var closedList = new HashSet<ThornPathNode>();
        var targetNode = new ThornPathNode(targetPosition);
        if (targetNode.X < 0 || targetNode.X >= _size || targetNode.Z < 0 || targetNode.Z >= _size) return null;
        _grid[targetNode.X, targetNode.Z] = targetNode;

        var startNode = new ThornPathNode(sourcePosition);
        if (startNode.X < 0 || startNode.X >= _size || startNode.Z < 0 || startNode.Z >= _size) return null;
        _grid[startNode.X, startNode.Z] = startNode;
        _openList.Enqueue(startNode);

        while (_openList.Count > 0)
        {
            var currentNode = _openList.Dequeue();
            if (currentNode == targetNode)
            {
                Clear();
                return RetracePath(startNode, targetNode);
            }
            closedList.Add(currentNode);
            for (var i = 0; i < 8; i++)
            {
                var x = currentNode.X + Direction[i, 0];
                var z = currentNode.Z + Direction[i, 1];
                if (x < 0 || x >= _size || z < 0 || z >= _size) continue;
                var neighbour = FindNodeOrCreate(x, z, currentNode.Position.y);
                if (!neighbour.Walkable) continue;
                if (i >= 4)
                {
                    var sideA = FindNodeOrCreate(currentNode.X + Direction[i, 0], currentNode.Z, currentNode.Position.y);
                    var sideB = FindNodeOrCreate(currentNode.X, currentNode.Z + Direction[i, 1], currentNode.Position.y);
                    if (!sideA.Walkable || !sideB.Walkable) continue;
                }
                var newGScore = currentNode.G + GetDistance(currentNode, neighbour);
                if (newGScore >= neighbour.G && closedList.Contains(neighbour)) continue;
                if (newGScore < neighbour.G || !_openList.Contains(neighbour))
                {
                    neighbour.G = newGScore;
                    neighbour.H = GetDistance(neighbour, targetNode);
                    neighbour.F = newGScore + neighbour.H;
                    neighbour.Parent = currentNode;
                    if (!_openList.Contains(neighbour)) _openList.Enqueue(neighbour);
                    else _openList.Update(neighbour);
                }
            }
            if (closedList.Count > MaxDepth) break;
        }
        Clear();
        return null;
    }

    private static void Clear()
    {
        _openList.Clear();
        Array.Clear(_grid, 0, _grid.Length);
    }

    private static List<Vector3> RetracePath(ThornPathNode startNode, ThornPathNode endNode)
    {
        var path = new List<Vector3>();
        while (endNode != startNode)
        {
            path.Add(endNode.Position);
            endNode = endNode.Parent;
        }
        path.Reverse();
        return path;
    }

    private static int GetDistance(ThornPathNode nodeA, ThornPathNode nodeB)
    {
        var dstX = Math.Abs(nodeA.X - nodeB.X);
        var dstZ = Math.Abs(nodeA.Z - nodeB.Z);
        var dstY = Math.Abs(nodeA.Position.y - nodeB.Position.y);
        if (dstX > dstZ) return 14 * dstZ + 10 * (dstX - dstZ) + (int)(10 * dstY);
        return 14 * dstX + 10 * (dstZ - dstX) + (int)(10 * dstY);
    }

    private static ThornPathNode FindNodeOrCreate(int x, int z, float y)
    {
        var node = _grid[x, z];
        if (node != null) return node;
        var halfGrid = _size / 2f;
        var groundPos = new Vector3(x - halfGrid, y, z - halfGrid);
        Vector3 resolved;
        FindRawGroundPosition(groundPos, out resolved);
        node = new ThornPathNode(resolved);
        _grid[x, z] = node;
        return node;
    }

    public static bool FindRawGroundPosition(Vector3 sourcePos, out Vector3 groundPos)
    {
        // Terrain height only. A ray could hit a monument roof and put the NPC on top of it.
        groundPos = sourcePos;
        groundPos.y = TerrainMeta.HeightMap.GetHeight(sourcePos);
        return true;
    }

    private static List<Vector3> FindLinePath(Vector3 sourcePosition, Vector3 targetPosition)
    {
        var distance = (int)Math.Ceiling(Vector3.Distance(sourcePosition, targetPosition));
        if (distance <= 0) return null;
        var straightPath = new List<Vector3>(new Vector3[distance]) { [distance - 1] = targetPosition };
        var currentPos = Vector3.Lerp(sourcePosition, targetPosition, 1f / distance);
        Vector3 groundPosition;
        if (!FindRawGroundPosition(currentPos, out groundPosition)) return null;
        if (IsWater(groundPosition) || !IsWalkableSlope(groundPosition)) return null;
        if (Vector3.Distance(groundPosition, sourcePosition) > 2) return null;
        if (SegmentBlocked(sourcePosition, groundPosition)) return null;
        straightPath[0] = groundPosition;
        for (var i = 1; i < distance - 1; i++)
        {
            currentPos = Vector3.Lerp(sourcePosition, targetPosition, (i + 1f) / distance);
            if (!FindRawGroundPosition(currentPos, out groundPosition)) return null;
            if (IsWater(groundPosition) || !IsWalkableSlope(groundPosition)) return null;
            if (Vector3.Distance(groundPosition, straightPath[i - 1]) > 2) return null;
            if (SegmentBlocked(straightPath[i - 1], groundPosition)) return null;
            straightPath[i] = groundPosition;
        }
        if (SegmentBlocked(distance == 1 ? sourcePosition : straightPath[distance - 2], targetPosition)) return null;
        return straightPath;
    }

    public sealed class ThornPathNode
    {
        public readonly int X;
        public readonly int Z;
        public int QueueIndex;
        public float H;
        public float G;
        public float F;
        public ThornPathNode Parent;
        public Vector3 Position;
        public bool Walkable;

        public ThornPathNode(Vector3 position)
        {
            Position = position;
            X = (int)Math.Round(position.x + _size / 2f);
            Z = (int)Math.Round(position.z + _size / 2f);
            Walkable = !Physics.CheckSphere(position + EyesPosition, .801f, _blockLayer)
                && !Physics.CheckSphere(position + KneeHeight, .4f, _blockLayer) && !IsWater(position) && IsWalkableSlope(position) && !HasRoofAbove(position);
        }
    }

    public sealed class ThornPriorityQueue
    {
        private readonly ThornPathNode[] _nodes;
        private int _numNodes;

        public ThornPriorityQueue(int maxNodes)
        {
            _numNodes = 0;
            _nodes = new ThornPathNode[maxNodes + 1];
        }

        public int Count => _numNodes;

        public void Clear()
        {
            Array.Clear(_nodes, 1, _numNodes);
            _numNodes = 0;
        }

        public bool Contains(ThornPathNode node) => _nodes[node.QueueIndex] == node;

        public void Update(ThornPathNode node) => SortUp(node);

        public void Enqueue(ThornPathNode node)
        {
            _nodes[++_numNodes] = node;
            node.QueueIndex = _numNodes;
            SortUp(node);
        }

        private void Swap(ThornPathNode node1, ThornPathNode node2)
        {
            _nodes[node1.QueueIndex] = node2;
            _nodes[node2.QueueIndex] = node1;
            var temp = node1.QueueIndex;
            node1.QueueIndex = node2.QueueIndex;
            node2.QueueIndex = temp;
        }

        private void SortUp(ThornPathNode node)
        {
            var parent = node.QueueIndex / 2;
            while (parent > 0)
            {
                var parentNode = _nodes[parent];
                if (CompareTo(parentNode, node) >= 0) break;
                Swap(node, parentNode);
                parent = node.QueueIndex / 2;
            }
        }

        private void SortDown(ThornPathNode node)
        {
            var finalQueueIndex = node.QueueIndex;
            while (true)
            {
                var newParent = node;
                var childLeftIndex = 2 * finalQueueIndex;
                if (childLeftIndex > _numNodes)
                {
                    node.QueueIndex = finalQueueIndex;
                    _nodes[finalQueueIndex] = node;
                    break;
                }
                var childLeft = _nodes[childLeftIndex];
                if (CompareTo(childLeft, newParent) >= 0) newParent = childLeft;
                var childRightIndex = childLeftIndex + 1;
                if (childRightIndex <= _numNodes)
                {
                    var childRight = _nodes[childRightIndex];
                    if (CompareTo(childRight, newParent) >= 0) newParent = childRight;
                }
                if (newParent != node)
                {
                    _nodes[finalQueueIndex] = newParent;
                    var temp = newParent.QueueIndex;
                    newParent.QueueIndex = finalQueueIndex;
                    finalQueueIndex = temp;
                }
                else
                {
                    node.QueueIndex = finalQueueIndex;
                    _nodes[finalQueueIndex] = node;
                    break;
                }
            }
        }

        public ThornPathNode Dequeue()
        {
            var node = _nodes[1];
            Remove(node);
            return node;
        }

        public void Remove(ThornPathNode node)
        {
            if (node.QueueIndex == _numNodes)
            {
                _nodes[_numNodes--] = null;
                return;
            }
            var formerLastNode = _nodes[_numNodes];
            Swap(node, formerLastNode);
            _nodes[_numNodes--] = null;
            var parentIndex = formerLastNode.QueueIndex / 2;
            var parentNode = _nodes[parentIndex];
            if (parentIndex > 0 && CompareTo(formerLastNode, parentNode) >= 0) SortUp(formerLastNode);
            else SortDown(formerLastNode);
        }

        private static int CompareTo(ThornPathNode node, ThornPathNode other)
        {
            if (node.F == other.F)
            {
                if (node.H == other.H) return 0;
                if (node.H > other.H) return -1;
                return 1;
            }
            if (node.F > other.F) return -1;
            return 1;
        }
    }
}

// Reference: ImageLibrary
// Reference: Shop

[Info("ThornNPC", "ThornFox", "1.0.0")]
[Description("Human NPCs with fixed target protection, a penalty on player damage and an admin GUI. / Menschen-NPCs mit festem Zielschutz, Strafe bei Spielerschaden und Admin-GUI.")]
public class ThornNPC : RustPlugin
{
    private const string PermAdmin = "thornnpc.admin";
    private const string PermCallNpcs = "thornnpc.callnpcs";
    private const string DataFileName = "ThornNPC";
    private const ulong UserIdBase = 76561198000000000UL;
    private const float NpcMaxHealth = 100f;
    private const int MinWaypoints = 3;
    private const int MaxWaypoints = 10;

    private static ThornNPC _ins;

    private ConfigData _config;
    private StoredData _data;
    private readonly NpcRegistry _registry = new NpcRegistry();
    private readonly PenaltyState _penalty = new PenaltyState();
    private readonly Dictionary<int, ThornNpcBehaviour> _live = new Dictionary<int, ThornNpcBehaviour>();
    private readonly Dictionary<int, Timer> _respawnTimers = new Dictionary<int, Timer>();
    private Timer _zoneTimer;
    // NPC ID -> player who is talking to it (E). The NPC stays still while the entry exists.
    private readonly Dictionary<int, ulong> _talking = new Dictionary<int, ulong>();

    #region Config

    private class ConfigData
    {
        // Config version. Bump when keys change and add a step in LoadConfig.
        public int ConfigVersion = 1;
        // true: a wipe (no save file, OnNewSave) clears the ThornNPC list. false: NPCs survive a wipe.
        public bool WipeNpcsOnNewSave = true;
        public int PenaltySeconds = 600;
        public float GuiRadius = 10f;

        public List<string> MessageHello = new List<string>
        {
            "Oh, ein neues Gesicht! Willkommen hier draußen.",
            "Na, hast du den Weg gefunden?",
            "Schön, dich zu sehen. Bleib einen Moment, ruh dich aus.",
            "Ah, du lebst also noch. Gute Nachricht!",
            "Komm rein, bevor es dunkel wird.",
            "Da bist du ja. Alles in Ordnung?",
            "Hey, Überlebender! Wie läuft’s da draußen?",
            "Wir nehmen jeden Verbündeten, den wir kriegen können.",
            "Setz dich, ich hab noch etwas Wasser übrig.",
            "Es ist gefährlich hier… bleib in meiner Nähe.",
            "Halt! Wer bist du?",
            "Nicht so schnell – ich will deine Hände sehen.",
            "Wenn du Ärger suchst, bist du hier falsch.",
            "Du siehst aus, als hättest du eine lange Reise hinter dir… oder Ärger.",
            "Einen Schritt weiter, und wir reden anders.",
            "Bist du allein hier?",
            "Ich weiß nicht, ob ich dir trauen kann.",
            "Bleib auf Abstand, Fremder.",
            "Du riechst nach Rauch… hast du was angezündet?",
            "Was willst du hier?",
            "Schließ die Tür! Sie könnten uns sehen!",
            "Du hast Glück, dass du noch lebst.",
            "Leise! Sie sind in der Nähe…",
            "Wir haben keine Zeit zu reden – pack deine Sachen.",
            "Hast du Vorräte mitgebracht?",
            "Schnell, hilf mir! Ich kann das nicht alleine schaffen.",
            "Da draußen tobt ein Sturm, besser bleibst du hier.",
            "Hast du Waffen bei dir? Wir brauchen jede Klinge.",
            "Die Nacht bricht gleich herein, beeil dich!",
            "Schließ das Tor hinter dir, sofort!",
            "Na, sieh mal einer an, der Held des Tages.",
            "Du siehst aus, als hättest du mit einem Bären gekämpft… und verloren.",
            "Hast du Essen mitgebracht, oder bist du nur zum Plaudern hier?",
            "Oh, du bist’s. Ich dachte, es wäre wieder ein hungriger Wolf.",
            "Keine Sorge, ich beiße nicht – meistens.",
            "Willkommen im Paradies… na ja, fast.",
            "Du hast überlebt? Beeindruckend. Manche schaffen nicht mal den Montag.",
            "Schön dich zu sehen. Weniger schön zu riechen… aber wir haben hier kein Parfüm.",
            "Falls du Brot hast – ich bin dein bester Freund.",
            "Und? Schon wieder knapp dem Tod entkommen?",
            "Wieder einer… mal sehen, wie lange du durchhältst.",
            "Genieß die Sonne. Wer weiß, wie viele Tage wir noch haben.",
            "Es gibt keinen sicheren Ort mehr.",
            "Sie kommen. Immer. Egal wohin du gehst.",
            "Ich hab zu viele sterben sehen, um noch Hoffnung zu haben.",
            "Jeder Tag ist geliehen… vergiss das nicht.",
            "Du bist spät. Manche haben es nicht geschafft.",
            "Alles, was wir tun, ist nur Aufschub.",
            "Ich hab aufgehört zu zählen, wie viele wir verloren haben.",
            "Willkommen… im letzten Kapitel der Menschheit."
        };

        public List<string> MessageBye = new List<string>
        {
            "Pass auf dich auf.",
            "Bis zum nächsten Mal, Überlebender.",
            "Bleib am Leben, egal was passiert.",
            "Wir sehen uns… hoffentlich.",
            "Mach keinen Ärger.",
            "Verschwinde, bevor es dunkel wird.",
            "Geh jetzt, bevor sie dich erwischen.",
            "Vergiss nicht, wo du hergekommen bist.",
            "Komm heil zurück.",
            "Möge der Wind auf deiner Seite sein.",
            "Halt dich von den Ruinen fern.",
            "Lass dich nicht erwischen.",
            "Hoffentlich triffst du unterwegs Freunde und keine Feinde.",
            "Bis bald – wenn wir Glück haben.",
            "Bleib wachsam.",
            "Die Nacht gehört nicht uns.",
            "Beeil dich, bevor der Sturm kommt.",
            "Ich hoffe, du findest, was du suchst.",
            "Vergiss nicht zu essen und zu trinken.",
            "Halt dich von den Schatten fern.",
            "Mach dich auf was gefasst.",
            "Ich werde nicht warten, wenn du zu spät kommst.",
            "Bleib auf der Straße – oder besser nicht.",
            "Sie werden dich jagen. Sei bereit.",
            "Jede Sekunde zählt. Geh jetzt.",
            "Hör auf dein Bauchgefühl.",
            "Vergiss nicht, die Tür hinter dir zu schließen.",
            "Du weißt, wo du mich findest.",
            "Komm nicht mit leeren Händen zurück.",
            "Man sieht sich – oder auch nicht.",
            "Überlebe… das ist alles, was zählt.",
            "Möge das Feuer dich wärmen.",
            "Wir werden sehen, wer von uns beiden länger durchhält.",
            "Versuch nicht, den Helden zu spielen.",
            "Vergiss nicht, wer deine Freunde sind.",
            "Nimm nur das Nötigste mit.",
            "Verlier keine Zeit.",
            "Die Welt da draußen frisst die Schwachen.",
            "Wenn du fällst, steh wieder auf.",
            "Geh jetzt – und schnell.",
            "Vielleicht sehen wir uns nicht wieder.",
            "Halt dich bedeckt.",
            "Wenn du zurückkommst, bring Geschichten mit.",
            "Jeder Tag könnte der letzte sein – nutze ihn.",
            "Mach keine Dummheiten.",
            "Lass dich nicht unterkriegen.",
            "Das Tor bleibt nicht ewig offen.",
            "Viel Glück – du wirst es brauchen.",
            "Geh, bevor jemand uns sieht."
        };

        public List<string> MessageUse = new List<string>
        {
            "Hm? Was gibt’s?",
            "Ja?",
            "Sprich schon.",
            "Ich höre.",
            "Was brauchst du?",
            "Red weiter.",
            "Du hast meine Aufmerksamkeit.",
            "Ist es wichtig?",
            "Mach’s kurz.",
            "Ich habe nicht den ganzen Tag.",
            "Komm zur Sache.",
            "Du störst mich gerade.",
            "Ich bin beschäftigt.",
            "Was ist los?",
            "Sag schon.",
            "Ja, ich bin hier.",
            "Hast du etwas für mich?",
            "Hast du Neuigkeiten?",
            "Ist etwas passiert?",
            "Willst du handeln?",
            "Wenn es um Vorräte geht, rede.",
            "Das hier besser wertvoll sein.",
            "Redest du oder stehst du nur rum?",
            "Das Wetter wird nicht besser, sprich schnell.",
            "Hast du was gefunden?",
            "Ich hoffe, du hast gute Nachrichten.",
            "Nicht schon wieder Smalltalk.",
            "Was willst du diesmal?",
            "Ich habe Wichtigeres zu tun.",
            "Wenn’s nicht dringend ist, geh.",
            "Sprich schnell, die Nacht kommt.",
            "Ich höre dir zu.",
            "Na, was ist?",
            "Willst du Hilfe?",
            "Brauchst du etwas?",
            "Ist das ein Notfall?",
            "Du wirkst nervös.",
            "Siehst aus, als hättest du was zu sagen.",
            "Komm schon, spuck’s aus.",
            "Überleg’s dir gut, was du sagst.",
            "Du bist schwer zu übersehen.",
            "Du bist wieder da.",
            "Das ging schnell.",
            "Schon zurück?",
            "Wieder Ärger?",
            "Bist du sicher, dass du hier reden willst?",
            "Pass auf, wer zuhört.",
            "Worum geht’s genau?",
            "Na schön, ich höre dir zu."
        };

        public List<string> MessageHurt = new List<string>
        {
            "Aua! Bist du verrückt?!",
            "Was zum…?!",
            "Hey! Pass auf!",
            "Willst du mich umbringen?!",
            "Das hat wehgetan!",
            "Warum tust du das?!",
            "Ich dachte, wir wären Verbündete!",
            "Ugh… verdammt…",
            "Blut… es hört nicht auf…",
            "Du Narr!",
            "Was stimmt nicht mit dir?!",
            "Lass das!",
            "Ich hab dir nichts getan!",
            "Das wirst du bereuen!",
            "Verräter!",
            "Ahh! Mein Arm!",
            "Geh weg von mir!",
            "Was soll das heißen, ‘aus Versehen’?!",
            "Du hättest mich fast umgebracht!",
            "Ich trau dir kein Stück mehr.",
            "Das ist dein Ernst?",
            "Bist du komplett durchgedreht?!",
            "Ugh… ich brauche… Hilfe…",
            "Nicht… nochmal…",
            "Ich… kann kaum… stehen…",
            "Du… Mistkerl…",
            "Warum ausgerechnet ich?",
            "Das… war ein Fehler…",
            "Ich… verliere… zu viel Blut…",
            "Du bist nicht besser als die da draußen!",
            "Lass… mich… in Ruhe…",
            "Rühr mich nicht mehr an!",
            "Halt Abstand!",
            "Ich dachte… wir hätten Frieden…",
            "Du… Feigling…",
            "Wenn ich überlebe…",
            "Ich… werde dich… finden…",
            "Du… solltest rennen…",
            "Hast du… kein Herz?",
            "Ich… kann kaum… atmen…",
            "Meine Rippen…",
            "Das… war… nicht nötig…",
            "Ugh… du… Dreckskerl…",
            "Es… endet… hier…",
            "Verdammt… warum?",
            "Ich… werd… dich… verfluchen…",
            "Besser… du verschwindest…",
            "Noch… ein Schlag… und…",
            "Das… ist… nicht… das Ende…"
        };
        public string NoteText = ThornConfig.DefaultNoteText;
        public Dictionary<string, NpcSettings> Npcs = new Dictionary<string, NpcSettings>();
    }

    private class NpcSettings
    {
        public int PenaltySeconds;
        public string NoteText;
        public string ChatMessage;
        public float ZoneRadius;
    }

    protected override void LoadDefaultConfig() => _config = new ConfigData();

    protected override void LoadConfig()
    {
        base.LoadConfig();
        try { _config = Config.ReadObject<ConfigData>(); }
        catch { _config = null; }
        if (_config == null) LoadDefaultConfig();
        // Migration steps by version. Version 1 is the first released layout, so no step exists yet.
        if (_config.ConfigVersion < 1) _config.ConfigVersion = 1;
        SaveConfig();
    }

    protected override void SaveConfig() => Config.WriteObject(_config, true);

    #endregion

    #region Data

    private class StoredNpc
    {
        public int Id;
        public string Name;
        public float X;
        public float Y;
        public float Z;
        public bool AllowSit = true;
        // Patrol points in order. The loop returns from the last point to the spawn point.
        public List<float[]> Waypoints = new List<float[]>();
        // Clothing from the wear bar only (not belt, not main inventory).
        public List<OutfitItem> Outfit = new List<OutfitItem>();
    }

    private class OutfitItem
    {
        public string Shortname;
        public ulong SkinId;
    }

    private class StoredData
    {
        public int LastId;
        public List<StoredNpc> Npcs = new List<StoredNpc>();
        // Unix seconds (UTC) when an active penalty ends, keyed by NPC ID.
        public Dictionary<string, long> PenaltyEndsUnix = new Dictionary<string, long>();
    }

    private void LoadData()
    {
        try { _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName); }
        catch { _data = null; }
        if (_data == null) _data = new StoredData();
    }

    private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);

    #endregion

    #region Lang

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["NoPermission"] = "You don't have permission to use this command.",
            ["NpcAdded"] = "NPC {0} added at your position.",
            ["NpcHitVanish"] = "You hit an NPC. It disappears for {0} minutes.",
            ["NpcNotFound"] = "NPC {0} not found.",
            ["UsageWay"] = "Usage: /npc_way <id> [clear]",
            ["WayMax"] = "NPC {0}: maximum {1} waypoints. Use /npc_way {0} clear to start over.",
            ["WayCountLow"] = "NPC {0}: {1} waypoint(s). At least {2} needed, otherwise it stays in place.",
            ["WayCount"] = "NPC {0}: {1} waypoint(s) of maximum {2}.",
            ["UsageRemove"] = "Usage: /npc_remove <id>",
            ["NpcRemoved"] = "NPC {0} removed.",
            ["UsageTp"] = "Usage: /npc_tp <id>",
            ["NpcTeleported"] = "NPC {0}: teleported to it.",
            ["UsageSpawn"] = "Usage: /npc_spawn <id> | /npc_spawn monument",
            ["NpcSpawnSet"] = "NPC {0}: spawn point set to your position, waypoints cleared.",
            ["NoMonuments"] = "No monuments found.",
            ["MonumentPlaced"] = "{0} of {1} NPC(s) placed at monuments.",
            ["CleanReport"] = "npc_clean: {0} figure(s) removed.",
            ["UsageZone"] = "Usage: /npc_zone <id> <radius> [chat text]",
            ["ZoneSet"] = "NPC {0}: zone radius {1} m.",
            ["CalledNpcs"] = "{0} NPC(s) called to you.",
            ["NoNpcInRadius"] = "No NPC within {0} m.",
            ["NpcNotInRadius"] = "NPC {0} is not within {1} m.",
            ["ShopUnlinked"] = "NPC {0}: shop link removed.",
            ["ShopPickOne"] = "Select at least one category.",
            ["ShopLinked"] = "NPC {0}: shop linked with {1} category(ies).",
            ["OutfitTaken"] = "NPC {0}: {1} clothing item(s) taken over.",
            ["GuiListTitle"] = "NPCs nearby ({0} m): choose one",
            ["GuiNoName"] = "no name",
            ["GuiMore"] = "{0} more NPC(s) not shown. Come closer to the NPCs.",
            ["GuiClose"] = "Close",
            ["GuiSettingsTitle"] = "ThornNPC  |  Settings",
            ["GuiId"] = "ID: {0}",
            ["GuiProtection"] = "Protection against heli, animals and NPCs: active (fixed)",
            ["GuiName"] = "Name:",
            ["GuiNote"] = "Note:",
            ["GuiPenalty"] = "Penalty: {0} min",
            ["GuiMinus1Min"] = "-1 min",
            ["GuiPlus1Min"] = "+1 min",
            ["GuiZone"] = "Zone radius: {0} m (0 = off)",
            ["GuiMinus1m"] = "-1 m",
            ["GuiPlus1m"] = "+1 m",
            ["GuiOutfit"] = "Clothing: {0} item(s) (wear bar)",
            ["GuiTakeOutfit"] = "Take over my clothing",
            ["GuiIdCardShop"] = "Shop: {0}",
            ["GuiNotLinked"] = "not linked",
            ["GuiShopUnreadable"] = "Shop not readable",
            ["GuiNoCategories"] = "no categories",
            ["GuiShopTitle"] = "Shop links of all NPCs",
            ["GuiPage"] = "Page {0} of {1}   ·   {2} NPC(s)",
            ["GuiNoNpcs"] = "No NPCs present.",
            ["GuiShopOn"] = "Shop on",
            ["GuiShopOff"] = "Shop off",
            ["GuiCategories"] = "Categories",
            ["GuiSave"] = "Save",
            ["GuiBack"] = "Back",
            ["GuiCatTitle"] = "Shop categories for NPC {0}",
            ["GuiCatHint"] = "Click categories to tick or untick them. At least one is needed.",
            ["GuiNoEnabledCats"] = "No enabled categories found in the Shop.",
        }, this, "en");

        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["NoPermission"] = "Dir fehlt die Berechtigung für diesen Befehl.",
            ["NpcAdded"] = "NPC {0} wurde an deiner Position angelegt.",
            ["NpcHitVanish"] = "Du hast einen NPC getroffen. Er verschwindet für {0} Minuten.",
            ["NpcNotFound"] = "NPC {0} nicht gefunden.",
            ["UsageWay"] = "Verwendung: /npc_way <id> [clear]",
            ["WayMax"] = "NPC {0}: maximal {1} Wegpunkte. Mit /npc_way {0} clear neu beginnen.",
            ["WayCountLow"] = "NPC {0}: {1} Wegpunkt(e). Mindestens {2} nötig, sonst bleibt er stehen.",
            ["WayCount"] = "NPC {0}: {1} Wegpunkt(e) von maximal {2}.",
            ["UsageRemove"] = "Verwendung: /npc_remove <id>",
            ["NpcRemoved"] = "NPC {0} entfernt.",
            ["UsageTp"] = "Verwendung: /npc_tp <id>",
            ["NpcTeleported"] = "NPC {0}: zu ihm teleportiert.",
            ["UsageSpawn"] = "Verwendung: /npc_spawn <id> | /npc_spawn monument",
            ["NpcSpawnSet"] = "NPC {0}: Spawnpunkt auf deine Position gesetzt, Wegpunkte gelöscht.",
            ["NoMonuments"] = "Keine Monumente gefunden.",
            ["MonumentPlaced"] = "{0} von {1} NPC(s) an Monumenten platziert.",
            ["CleanReport"] = "npc_clean: {0} Figur(en) entfernt.",
            ["UsageZone"] = "Verwendung: /npc_zone <id> <radius> [Chat-Text]",
            ["ZoneSet"] = "NPC {0}: Zonenradius {1} m.",
            ["CalledNpcs"] = "{0} NPC(s) zu dir gerufen.",
            ["NoNpcInRadius"] = "Kein NPC im Umkreis von {0} m.",
            ["NpcNotInRadius"] = "NPC {0} ist nicht im Umkreis von {1} m.",
            ["ShopUnlinked"] = "NPC {0}: Shop-Verknüpfung entfernt.",
            ["ShopPickOne"] = "Wähle mindestens eine Kategorie.",
            ["ShopLinked"] = "NPC {0}: Shop mit {1} Kategorie(n) verknüpft.",
            ["OutfitTaken"] = "NPC {0}: {1} Kleidungsstück(e) übernommen.",
            ["GuiListTitle"] = "NPC in der Nähe ({0} m): Auswahl",
            ["GuiNoName"] = "ohne Namen",
            ["GuiMore"] = "{0} weitere NPC(s) nicht angezeigt. Stell dich näher an die NPCs.",
            ["GuiClose"] = "Schließen",
            ["GuiSettingsTitle"] = "ThornNPC  |  Einstellungen",
            ["GuiId"] = "ID: {0}",
            ["GuiProtection"] = "Schutz gegen Heli, Tiere und NPCs: aktiv (fest)",
            ["GuiName"] = "Name:",
            ["GuiNote"] = "Zettel:",
            ["GuiPenalty"] = "Strafzeit: {0} min",
            ["GuiMinus1Min"] = "-1 min",
            ["GuiPlus1Min"] = "+1 min",
            ["GuiZone"] = "Zonenradius: {0} m (0 = aus)",
            ["GuiMinus1m"] = "-1 m",
            ["GuiPlus1m"] = "+1 m",
            ["GuiOutfit"] = "Kleidung: {0} Teil(e) (Kleiderleiste)",
            ["GuiTakeOutfit"] = "Meine Kleidung übernehmen",
            ["GuiIdCardShop"] = "Shop: {0}",
            ["GuiNotLinked"] = "nicht verknüpft",
            ["GuiShopUnreadable"] = "Shop nicht lesbar",
            ["GuiNoCategories"] = "keine Kategorien",
            ["GuiShopTitle"] = "Shop-Verknüpfungen aller NPCs",
            ["GuiPage"] = "Seite {0} von {1}   ·   {2} NPC(s)",
            ["GuiNoNpcs"] = "Keine NPCs vorhanden.",
            ["GuiShopOn"] = "Shop an",
            ["GuiShopOff"] = "Shop aus",
            ["GuiCategories"] = "Kategorien",
            ["GuiSave"] = "Speichern",
            ["GuiBack"] = "Zurück",
            ["GuiCatTitle"] = "Shop-Kategorien für NPC {0}",
            ["GuiCatHint"] = "Klicke auf Kategorien, um sie an- oder abzuwählen. Mindestens eine ist nötig.",
            ["GuiNoEnabledCats"] = "Keine aktivierten Kategorien im Shop gefunden.",
        }, this, "de");
    }

    private string Msg(BasePlayer player, string key, params object[] args)
    {
        var text = lang.GetMessage(key, this, player.UserIDString);
        return args.Length == 0 ? text : string.Format(text, args);
    }

    #endregion

    #region Lifecycle

    private void Init()
    {
        _ins = this;
        permission.RegisterPermission(PermAdmin, this);
        permission.RegisterPermission(PermCallNpcs, this);
    }

    private void OnServerInitialized()
    {
        LoadData();
        _registry.KeepLastIdAtLeast(_data.LastId);
        KillNpcEntities(keepLive: true);
        ThornPath.Init();
        _zoneTimer = timer.Every(1f, CheckZones);
        foreach (var stored in _data.Npcs)
        {
            _registry.Restore(new NpcRecord(stored.Id, new Pos(stored.X, stored.Y, stored.Z)));
        }
        foreach (var stored in _data.Npcs)
        {
            var rec = _registry.Get(stored.Id);
            if (rec == null) continue;
            long endUnix;
            if (_data.PenaltyEndsUnix.TryGetValue(stored.Id.ToString(), out endUnix))
            {
                var ends = FromUnix(endUnix);
                var now = DateTime.UtcNow;
                if (ends > now)
                {
                    _penalty.TryStart(stored.Id, now, ends - now);
                    ScheduleRespawn(stored.Id, ends - now);
                    continue;
                }
                _data.PenaltyEndsUnix.Remove(stored.Id.ToString());
            }
            SpawnNpc(rec, stored);
        }
        SaveData();
    }

    private void OnServerSave() => SaveData();

    // No save file means a new map or a wipe. With WipeNpcsOnNewSave the ThornNPC list is cleared.
    // LastId is kept, so IDs are not reused and old shop links never point at a new NPC.
    private void OnNewSave(string strFilename)
    {
        if (_config == null || !_config.WipeNpcsOnNewSave) return;
        if (_data == null) LoadData();

        foreach (var t in _respawnTimers.Values) t?.Destroy();
        _respawnTimers.Clear();
        foreach (var npc in _live.Values.ToList()) Vanish(npc);
        _registry.Clear();
        _penalty.ClearAll();
        _inZone.Clear();
        _talking.Clear();
        _data.Npcs.Clear();
        _data.PenaltyEndsUnix.Clear();
        _config.Npcs.Clear();
        SaveConfig();
        SaveData();
        PrintWarning("Wipe detected: ThornNPC list cleared (WipeNpcsOnNewSave).");
    }

    // Removes ThornNPC player entities from the world. With keepLive, the ones this instance
    // tracks are kept. Without it, all are removed (used on unload).
    // Needed because a reload leaves the previous instance's NPCs in the world; they would
    // stay as duplicates next to the freshly spawned ones.
    private void KillNpcEntities(bool keepLive)
    {
        var found = new List<BasePlayer>();
        foreach (BaseNetworkable net in BaseNetworkable.serverEntities)
        {
            var player = net as BasePlayer;
            if (player == null || player.IsDestroyed) continue;
            var uid = player.userID;
            if (uid < UserIdBase || uid >= UserIdBase + 1000000UL) continue;
            if (keepLive)
            {
                ThornNpcBehaviour live;
                if (_live.TryGetValue((int)(uid - UserIdBase), out live) && live.Player == player) continue;
            }
            found.Add(player);
        }
        foreach (var player in found) player.Kill(BaseNetworkable.DestroyMode.None);
        if (found.Count > 0) Puts($"Removed {found.Count} ThornNPC entity(ies) from the world.");
    }

    private void Unload()
    {
        foreach (var t in _respawnTimers.Values) t?.Destroy();
        _respawnTimers.Clear();
        _zoneTimer?.Destroy();
        foreach (var p in BasePlayer.activePlayerList)
        {
            if (p == null) continue;
            CuiHelper.DestroyUi(p, GuiName);
            CuiHelper.DestroyUi(p, CardName);
            CuiHelper.DestroyUi(p, ShopGuiName);
        }
        _inZone.Clear();
        _shopEdit.Clear();
        _shopPage.Clear();
        _talking.Clear();
        KillNpcEntities(keepLive: false);
        _live.Clear();
        SaveData();
        ThornPath.Shutdown();
        _ins = null;
    }

    #endregion

    #region Spawn

    private static ulong UserIdFor(int id) => UserIdBase + (ulong)id;

    private void SpawnNpc(NpcRecord rec, StoredNpc stored)
    {
        if (rec == null || stored == null || _live.ContainsKey(rec.Id)) return;
        var pos = new Vector3(rec.Spawn.X, rec.Spawn.Y, rec.Spawn.Z);
        pos.y = ThornLocomotion.GroundY(pos, pos.y);
        var player = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", pos, Quaternion.identity) as BasePlayer;
        if (player == null)
        {
            PrintError($"Could not create NPC player entity for ID {rec.Id}.");
            return;
        }
        player.userID = UserIdFor(rec.Id);
        player.UserIDString = player.userID.ToString();
        player.displayName = stored.Name;
        player.syncPosition = true;

        var behaviour = player.gameObject.AddComponent<ThornNpcBehaviour>();
        behaviour.Setup(rec.Id, player);

        var locomotion = player.gameObject.AddComponent<ThornLocomotion>();
        locomotion.Setup(player, stored.AllowSit);
        locomotion.SetRoute(BuildRoute(rec, stored));
        if (_talking.ContainsKey(rec.Id)) locomotion.Pause();

        player.Spawn();
        ApplyOutfit(player, stored);
        player.InitializeHealth(NpcMaxHealth, NpcMaxHealth);
        _live[rec.Id] = behaviour;
    }

    // Puts the stored clothing into the NPC's wear bar (called right after Spawn).
    private static void ApplyOutfit(BasePlayer player, StoredNpc stored)
    {
        if (stored.Outfit == null || stored.Outfit.Count == 0 || player.inventory == null) return;
        foreach (var entry in stored.Outfit)
        {
            var item = ItemManager.CreateByName(entry.Shortname, 1, entry.SkinId);
            if (item == null) continue;
            if (!item.MoveToContainer(player.inventory.containerWear)) item.Remove();
        }
    }

    // Copies the admin's wear bar (clothing only) to the NPC and respawns it with that outfit.
    private void CaptureOutfit(BasePlayer admin, int id)
    {
        var stored = FindStored(id);
        if (stored == null) return;
        stored.Outfit = admin.inventory.containerWear.itemList
            .Where(i => i != null && i.info != null)
            .Select(i => new OutfitItem { Shortname = i.info.shortname, SkinId = i.skin })
            .ToList();
        SaveData();
        RespawnLive(id);
        admin.ChatMessage(Msg(admin, "OutfitTaken", id, stored.Outfit.Count));
    }

    // Changes the name in data and on the live NPC.
    private void RenameNpc(int id, string name)
    {
        var stored = FindStored(id);
        if (stored == null) return;
        stored.Name = name;
        SaveData();
        // Name is only sent to clients on spawn, so a live NPC is respawned to show the new name.
        RespawnLive(id);
    }

    // Spawn -> waypoint 1 -> ... -> last waypoint -> spawn. Each leg goes through ThornPath.Go.
    private static List<Vector3> BuildRoute(NpcRecord rec, StoredNpc stored)
    {
        var route = new List<Vector3>();
        // Fewer than MinWaypoints: the NPC stays at its spawn point.
        if (stored.Waypoints == null || stored.Waypoints.Count < MinWaypoints) return route;

        var from = new Vector3(rec.Spawn.X, rec.Spawn.Y, rec.Spawn.Z);
        var points = stored.Waypoints.Select(w => new Vector3(w[0], w[1], w[2])).ToList();
        points.Add(from);
        foreach (var target in points)
        {
            var leg = ThornPath.Go(from, target) ?? new List<Vector3> { target };
            route.AddRange(leg);
            from = target;
        }
        return route;
    }

    private StoredNpc FindStored(int id) => _data.Npcs.FirstOrDefault(n => n.Id == id);

    private static Pos ToPos(Vector3 v) => new Pos(v.x, v.y, v.z);

    private static long ToUnix(DateTime utc) => (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    private static DateTime FromUnix(long unix) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix);

    public class ThornNpcBehaviour : MonoBehaviour
    {
        public int Id { get; private set; }
        public BasePlayer Player { get; private set; }

        public void Setup(int id, BasePlayer player)
        {
            Id = id;
            Player = player;
        }
    }

    #endregion

    #region Protection (fixed, always on)

    private static bool IsThornNpc(BaseEntity entity) =>
        entity is BasePlayer player && player.GetComponent<ThornNpcBehaviour>() != null;

    // Heli cannot target ThornNPCs (PatrolHelicopterAI.PlayerVisible). false blocks.
    private bool? CanHelicopterTarget(PatrolHelicopterAI heli, BasePlayer ply) => IsThornNpc(ply) ? (bool?)false : null;

    // Heli cannot strafe ThornNPCs (PatrolHelicopterAI.ValidRocketTarget). false blocks.
    private bool? CanHelicopterStrafeTarget(PatrolHelicopterAI heli, BasePlayer ply) => IsThornNpc(ply) ? (bool?)false : null;

    // Bradley APC (tank) cannot see or target ThornNPCs (BradleyAPC.VisibilityTest). false blocks.
    private bool? CanBradleyApcTarget(BradleyAPC apc, BaseEntity ent) => IsThornNpc(ent) ? (bool?)false : null;

    // Heli turrets never take a ThornNPC as target. Non-null blocks the assignment.
    private object OnHelicopterTarget(HelicopterTurret turret, BaseCombatEntity target) =>
        IsThornNpc(target) ? (object)true : null;

    // Animals and NPCs never target a ThornNPC. Non-null blocks the target.
    private object OnNpcTarget(BaseEntity owner, BaseEntity entity) => IsThornNpc(entity) ? (object)true : null;

    // NPCs never even sense a ThornNPC as a threat (SimpleAIMemory.SetKnown). Non-null blocks it.
    private object OnNpcTargetSense(BaseEntity owner, BaseEntity ent, AIBrainSenses brainSenses) =>
        IsThornNpc(ent) ? (object)true : null;

    // NpcSpawn NPCs (for example "Space Mercenary" presets) decide their targets in their own brain
    // and ask this hook first. A bool refuses the target, so a ThornNPC is never chosen.
    private object OnCustomNpcTarget(object attacker, BaseCombatEntity target) =>
        IsThornNpc(target) ? (object)false : null;

    // NPCs never start an attack on a ThornNPC (BaseNpc.StartAttack). Non-null blocks it.
    private object OnNpcAttack(BaseNpc instance, BaseEntity attackTarget) =>
        IsThornNpc(attackTarget) ? (object)true : null;

    // Invulnerable: damage is always overridden. A hit by a real player starts the penalty.
    private object OnEntityTakeDamage(BasePlayer basePlayer, HitInfo hitInfo)
    {
        var npc = basePlayer == null ? null : basePlayer.GetComponent<ThornNpcBehaviour>();
        if (npc == null) return null;

        var attacker = hitInfo?.InitiatorPlayer;
        if (attacker != null && attacker.userID.IsSteamId())
        {
            var attackerId = attacker.userID;
            NextTick(() => ApplyPenalty(npc, attackerId));
        }
        return true;
    }

    #endregion

    #region Penalty

    private int ResolvePenaltySeconds(int id)
    {
        NpcSettings settings;
        if (_config.Npcs.TryGetValue(id.ToString(), out settings) && settings != null && settings.PenaltySeconds > 0)
            return settings.PenaltySeconds;
        return _config.PenaltySeconds;
    }

    private string ResolveNoteText(int id)
    {
        NpcSettings settings;
        if (_config.Npcs.TryGetValue(id.ToString(), out settings) && !string.IsNullOrEmpty(settings?.NoteText))
            return settings.NoteText;
        return _config.NoteText;
    }

    private void ApplyPenalty(ThornNpcBehaviour npc, ulong attackerId)
    {
        if (npc == null || npc.Player == null) return;
        var duration = ThornConfig.ClampPenalty(TimeSpan.FromSeconds(ResolvePenaltySeconds(npc.Id)));
        var now = DateTime.UtcNow;
        if (!_penalty.TryStart(npc.Id, now, duration)) return;

        _data.PenaltyEndsUnix[npc.Id.ToString()] = ToUnix(now + duration);
        SaveData();

        DropNote(npc);
        Vanish(npc);
        ScheduleRespawn(npc.Id, duration);

        var attacker = BasePlayer.FindByID(attackerId);
        if (attacker != null) attacker.ChatMessage(Msg(attacker, "NpcHitVanish", (int)duration.TotalMinutes));
        var hurt = RandomLine(_config.MessageHurt);
        if (attacker != null && !string.IsNullOrEmpty(hurt)) attacker.ChatMessage(NpcTalk(npc.Id, hurt));
    }

    private void DropNote(ThornNpcBehaviour npc)
    {
        var item = ItemManager.CreateByName("note", 1);
        if (item == null)
        {
            PrintError("Item 'note' not found, no note dropped.");
            return;
        }
        item.text = ResolveNoteText(npc.Id);
        item.Drop(npc.Player.transform.position + new Vector3(0f, 0.2f, 0f), Vector3.zero);
    }

    // Removes the NPC entity without a death: no Die(), no loot.
    private void Vanish(ThornNpcBehaviour npc)
    {
        _live.Remove(npc.Id);
        if (npc.Player != null && !npc.Player.IsDestroyed)
            npc.Player.Kill(BaseNetworkable.DestroyMode.None);
    }

    private void ScheduleRespawn(int id, TimeSpan delay)
    {
        Timer existing;
        if (_respawnTimers.TryGetValue(id, out existing)) existing?.Destroy();
        _respawnTimers[id] = timer.Once((float)Math.Max(1, delay.TotalSeconds), () => Respawn(id));
    }

    private void Respawn(int id)
    {
        _respawnTimers.Remove(id);
        _penalty.Clear(id);
        _data.PenaltyEndsUnix.Remove(id.ToString());
        SaveData();

        var rec = _registry.Get(id);
        var stored = FindStored(id);
        if (rec != null && stored != null) SpawnNpc(rec, stored);
    }

    #endregion

    #region Commands (test stage, /npc_add only)

    [ChatCommand("npc_add")]
    private void CmdNpcAdd(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }

        var id = _registry.Add(ToPos(player.transform.position));
        var name = $"NPC {id}";
        _data.LastId = id;
        _data.Npcs.Add(new StoredNpc
        {
            Id = id,
            Name = name,
            X = player.transform.position.x,
            Y = player.transform.position.y,
            Z = player.transform.position.z
        });
        SaveData();
        SpawnNpc(_registry.Get(id), _data.Npcs.Last());
        player.ChatMessage(Msg(player, "NpcAdded", id));
    }

    // /npc_way <id>            adds the current position as next patrol point
    // /npc_way <id> clear      removes all patrol points
    [ChatCommand("npc_way")]
    private void CmdNpcWay(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        int id;
        if (args.Length < 1 || !int.TryParse(args[0], out id))
        {
            player.ChatMessage(Msg(player, "UsageWay"));
            return;
        }
        var stored = FindStored(id);
        if (stored == null)
        {
            player.ChatMessage(Msg(player, "NpcNotFound", id));
            return;
        }
        if (args.Length > 1 && args[1].ToLower() == "clear")
        {
            stored.Waypoints.Clear();
        }
        else
        {
            if (stored.Waypoints.Count >= MaxWaypoints)
            {
                player.ChatMessage(Msg(player, "WayMax", id, MaxWaypoints));
                return;
            }
            var p = player.transform.position;
            stored.Waypoints.Add(new[] { p.x, p.y, p.z });
        }
        SaveData();
        RespawnLive(id);
        var count = stored.Waypoints.Count;
        player.ChatMessage(count < MinWaypoints
            ? Msg(player, "WayCountLow", id, count, MinWaypoints)
            : Msg(player, "WayCount", id, count, MaxWaypoints));
    }

    // /npc_remove <id>: removes the NPC from data, config and the live world.
    [ChatCommand("npc_remove")]
    private void CmdNpcRemove(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        int id;
        if (args.Length < 1 || !int.TryParse(args[0], out id))
        {
            player.ChatMessage(Msg(player, "UsageRemove"));
            return;
        }
        var stored = FindStored(id);
        if (stored == null)
        {
            player.ChatMessage(Msg(player, "NpcNotFound", id));
            return;
        }

        ThornNpcBehaviour live;
        if (_live.TryGetValue(id, out live))
        {
            Vanish(live);
        }
        Timer t;
        if (_respawnTimers.TryGetValue(id, out t)) t?.Destroy();
        _respawnTimers.Remove(id);
        _penalty.Clear(id);
        _registry.Remove(id);
        _data.Npcs.Remove(stored);
        _data.PenaltyEndsUnix.Remove(id.ToString());
        _config.Npcs.Remove(id.ToString());
        SaveConfig();
        SaveData();
        player.ChatMessage(Msg(player, "NpcRemoved", id));
    }

    // Rebuilds the live NPC so changed waypoints take effect immediately.
    private void RespawnLive(int id)
    {
        ThornNpcBehaviour live;
        if (_live.TryGetValue(id, out live)) Vanish(live);
        var rec = _registry.Get(id);
        var stored = FindStored(id);
        if (rec != null && stored != null && !_penalty.IsActive(id, DateTime.UtcNow)) SpawnNpc(rec, stored);
    }

    #endregion

    #region Teleport (/npc_tp)

    // /npc_tp <id>: teleports you next to the NPC. Uses the live figure, or its spawn point if it is away.
    [ChatCommand("npc_tp")]
    private void CmdNpcTp(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        int id;
        if (args.Length < 1 || !int.TryParse(args[0], out id))
        {
            player.ChatMessage(Msg(player, "UsageTp"));
            return;
        }
        var stored = FindStored(id);
        if (stored == null)
        {
            player.ChatMessage(Msg(player, "NpcNotFound", id));
            return;
        }

        Vector3 target;
        ThornNpcBehaviour live;
        if (_live.TryGetValue(id, out live) && live.Player != null)
            target = live.Player.transform.position;
        else
            target = new Vector3(stored.X, stored.Y, stored.Z);

        player.Teleport(target + new Vector3(1.5f, 0f, 0f));
        player.ChatMessage(Msg(player, "NpcTeleported", id));
    }

    #endregion

    #region Spawn points (/npc_spawn)

    // /npc_spawn <id>           moves the NPC's spawn point to your position and clears its waypoints,
    //                           so the route does not lead it back to the old points
    // /npc_spawn monument       spreads all NPCs over random monuments (waypoints are cleared)
    [ChatCommand("npc_spawn")]
    private void CmdNpcSpawn(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        if (args.Length < 1)
        {
            player.ChatMessage(Msg(player, "UsageSpawn"));
            return;
        }
        if (args[0].ToLower() == "monument")
        {
            SpawnAtMonuments(player);
            return;
        }
        int id;
        if (!int.TryParse(args[0], out id) || FindStored(id) == null)
        {
            player.ChatMessage(Msg(player, "NpcNotFound", args[0]));
            return;
        }
        var p = player.transform.position;
        SetNpcSpawn(id, new Vector3(p.x, p.y, p.z), clearWaypoints: true);
        player.ChatMessage(Msg(player, "NpcSpawnSet", id));
    }

    // Stores a new spawn point (on the ground) and respawns the NPC there.
    private void SetNpcSpawn(int id, Vector3 position, bool clearWaypoints)
    {
        var stored = FindStored(id);
        if (stored == null) return;
        position.y = ThornLocomotion.GroundY(position, position.y);
        stored.X = position.x;
        stored.Y = position.y;
        stored.Z = position.z;
        if (clearWaypoints) stored.Waypoints.Clear();
        _registry.Restore(new NpcRecord(id, ToPos(position)));
        SaveData();
        RespawnLive(id);
    }

    private void SpawnAtMonuments(BasePlayer player)
    {
        var monuments = TerrainMeta.Path.Monuments;
        if (monuments == null || monuments.Count == 0)
        {
            player.ChatMessage(Msg(player, "NoMonuments"));
            return;
        }
        var rnd = new System.Random();
        var placed = 0;
        foreach (var stored in _data.Npcs.ToList())
        {
            var monument = monuments[rnd.Next(monuments.Count)];
            var spot = FindMonumentSpot(monument.transform.position, rnd);
            if (spot == null) continue;
            SetNpcSpawn(stored.Id, spot.Value, clearWaypoints: true);
            // Four random patrol points around the spawn, so the NPC walks a round in the monument.
            var npc = FindStored(stored.Id);
            for (var w = 0; w < 4; w++)
            {
                var point = FindMonumentSpot(spot.Value, rnd, 25f);
                if (point == null) continue;
                npc.Waypoints.Add(new[] { point.Value.x, point.Value.y, point.Value.z });
            }
            // Fewer than the minimum of 3 points: no patrol at all, the NPC stays at its spawn.
            if (npc.Waypoints.Count < MinWaypoints) npc.Waypoints.Clear();
            SaveData();
            RespawnLive(stored.Id);
            placed++;
        }
        player.ChatMessage(Msg(player, "MonumentPlaced", placed, _data.Npcs.Count));
    }

    // A walkable spot within 20 m of the monument centre (no water, no roof above).
    private static Vector3? FindMonumentSpot(Vector3 centre, System.Random rnd, float radius = 20f)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var p = new Vector3(
                centre.x + (float)(rnd.NextDouble() * 2.0 * radius - radius),
                0f,
                centre.z + (float)(rnd.NextDouble() * 2.0 * radius - radius));
            p.y = ThornLocomotion.GroundY(p, 0f);
            if (ThornPath.IsWater(p) || !ThornPath.IsWalkableSlope(p) || ThornPath.HasRoofAbove(p)) continue;
            return p;
        }
        return null;
    }

    #endregion

    #region Cleanup (/npc_clean)

    // /npc_clean: removes ThornNPC figures the plugin no longer tracks (orphans), server-wide,
    // and lists where they stood. /npc_clean all also removes the tracked ones.
    [ChatCommand("npc_clean")]
    private void CmdNpcClean(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        var all = args.Length > 0 && args[0].ToLower() == "all";

        var found = new List<BasePlayer>();
        foreach (BaseNetworkable net in BaseNetworkable.serverEntities)
        {
            var p = net as BasePlayer;
            if (p == null || p.IsDestroyed) continue;
            if (p.userID < UserIdBase || p.userID >= UserIdBase + 1000000UL) continue;
            ThornNpcBehaviour live;
            var tracked = _live.TryGetValue((int)(p.userID - UserIdBase), out live) && live.Player == p;
            if (tracked && !all) continue;
            found.Add(p);
        }

        var report = new List<string>();
        foreach (var p in found)
        {
            var pos = p.transform.position;
            var id = (int)(p.userID - UserIdBase);
            _live.Remove(id);
            p.Kill(BaseNetworkable.DestroyMode.None);
            var state = p.IsDestroyed ? "destroyed" : "STILL EXISTS";
            report.Add($"ID {id} at ({pos.x:0}, {pos.y:0.0}, {pos.z:0}) {state}");
        }

        player.ChatMessage(Msg(player, "CleanReport", found.Count));
        foreach (var line in report.Take(10)) player.ChatMessage(line);
        Puts($"npc_clean removed {found.Count}: {string.Join("; ", report)}");
    }

    #endregion

    #region Interaction: E (shop), zones, /callnpcs

    // E on a ThornNPC: stands up if seated, turns to the player, fires OnUseNPC for the shop.
    // E never opens the admin GUI.
    private void OnPlayerInput(BasePlayer player, InputState input)
    {
        if (player == null || input == null || !input.WasJustPressed(BUTTON.USE)) return;
        if (player.GetComponent<ThornNpcBehaviour>() != null) return;

        var ray = player.eyes.HeadRay();
        var hits = Physics.RaycastAll(ray, 3f);
        foreach (var hit in hits.OrderBy(h => h.distance))
        {
            var npc = hit.collider.GetComponentInParent<ThornNpcBehaviour>();
            if (npc == null || npc.Player == null) continue;

            var locomotion = npc.Player.GetComponent<ThornLocomotion>();
            if (locomotion != null)
            {
                locomotion.Stand();
                locomotion.Pause();
            }
            _talking[npc.Id] = player.userID;
            FaceTowards(npc.Player, player.transform.position);
            Interface.Oxide.CallHook("OnUseNPC", npc.Player, player);
            var use = RandomLine(_config.MessageUse);
            if (!string.IsNullOrEmpty(use)) player.ChatMessage(NpcTalk(npc.Id, use));
            return;
        }
    }

    private static void FaceTowards(BasePlayer npc, Vector3 target)
    {
        var dir = target - npc.transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;
        npc.transform.rotation = Quaternion.LookRotation(dir);
        npc.SendNetworkUpdate();
    }

    // Zones: a ThornNPC with ZoneRadius > 0 sends ChatMessage to players entering the radius
    // and fires OnThornNpcZoneEnter / OnThornNpcZoneLeave for other plugins.
    private readonly Dictionary<int, HashSet<ulong>> _inZone = new Dictionary<int, HashSet<ulong>>();

    // A talking NPC stands still while the player is within ConversationRange, and walks on once the player moved away.
    // The plugin cannot see the shop menu closing, so distance is the signal to resume.
    private const float ConversationRange = 5f;

    private void UpdateConversations()
    {
        foreach (var id in _talking.Keys.ToList())
        {
            var player = BasePlayer.FindByID(_talking[id]);
            ThornNpcBehaviour npc;
            if (!_live.TryGetValue(id, out npc) || npc.Player == null)
            {
                _talking.Remove(id);
                continue;
            }
            var locomotion = npc.Player.GetComponent<ThornLocomotion>();
            var away = player == null || player.IsDead()
                || Vector3.Distance(player.transform.position, npc.Player.transform.position) > ConversationRange;
            if (!away) continue;
            _talking.Remove(id);
            if (locomotion != null) locomotion.Resume();
        }
    }

    private void CheckZones()
    {
        UpdateConversations();
        foreach (var npc in _live.Values.ToList())
        {
            if (npc.Player == null) continue;
            var radius = ResolveZoneRadius(npc.Id);
            if (radius <= 0f) continue;

            HashSet<ulong> inside;
            if (!_inZone.TryGetValue(npc.Id, out inside)) _inZone[npc.Id] = inside = new HashSet<ulong>();

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead()) continue;
                var near = Vector3.Distance(player.transform.position, npc.Player.transform.position) <= radius;
                var was = inside.Contains(player.userID);
                if (near && !was)
                {
                    inside.Add(player.userID);
                    var msg = ResolveChatMessage(npc.Id) ?? RandomLine(_config.MessageHello);
                    if (!string.IsNullOrEmpty(msg)) player.ChatMessage(msg);
                    Interface.Oxide.CallHook("OnThornNpcZoneEnter", player, npc.Id);
                }
                else if (!near && was)
                {
                    inside.Remove(player.userID);
                    Interface.Oxide.CallHook("OnThornNpcZoneLeave", player, npc.Id);
                    var bye = RandomLine(_config.MessageBye);
                    if (!string.IsNullOrEmpty(bye)) player.ChatMessage(NpcTalk(npc.Id, bye));
                }
            }
        }
    }

    private float ResolveZoneRadius(int id)
    {
        NpcSettings s;
        return _config.Npcs.TryGetValue(id.ToString(), out s) && s != null ? s.ZoneRadius : 0f;
    }

    private string ResolveChatMessage(int id)
    {
        NpcSettings s;
        return _config.Npcs.TryGetValue(id.ToString(), out s) && s != null ? s.ChatMessage : null;
    }

    // /npc_zone <id> <radius> [chat text...]   radius 0 switches the zone off
    [ChatCommand("npc_zone")]
    private void CmdNpcZone(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        float radius;
        int id;
        if (args.Length < 2 || !int.TryParse(args[0], out id) || !float.TryParse(args[1], out radius))
        {
            player.ChatMessage(Msg(player, "UsageZone"));
            return;
        }
        if (FindStored(id) == null)
        {
            player.ChatMessage(Msg(player, "NpcNotFound", id));
            return;
        }
        NpcSettings s = GetOrCreateSettings(id);
        s.ZoneRadius = Math.Max(0f, radius);
        s.ChatMessage = args.Length > 2 ? string.Join(" ", args.Skip(2)) : s.ChatMessage;
        SaveConfig();
        player.ChatMessage(Msg(player, "ZoneSet", id, s.ZoneRadius));
    }

    private NpcSettings GetOrCreateSettings(int id)
    {
        NpcSettings s;
        if (!_config.Npcs.TryGetValue(id.ToString(), out s) || s == null)
        {
            s = new NpcSettings();
            _config.Npcs[id.ToString()] = s;
        }
        return s;
    }

    // /callnpcs (from HumanNPCCallAll): every ThornNPC walks to the caller, gets the caller's position as spawn point,
    // and its waypoints are cleared (as with /npc_spawn).
    [ChatCommand("callnpcs")]
    private void CmdCallNpcs(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermCallNpcs))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        var p = player.transform.position;
        var moved = 0;
        foreach (var stored in _data.Npcs.ToList())
        {
            if (_penalty.IsActive(stored.Id, DateTime.UtcNow)) continue;
            stored.X = p.x;
            stored.Y = p.y;
            stored.Z = p.z;
            // Old waypoints would lead the NPC back to where it was, so they are cleared.
            stored.Waypoints.Clear();
            _registry.Restore(new NpcRecord(stored.Id, ToPos(p)));
            RespawnLive(stored.Id);
            moved++;
        }
        SaveData();
        player.ChatMessage(Msg(player, "CalledNpcs", moved));
    }

    #endregion

    #region Admin GUI (/npc_gui)

    // Step 1: finds NPCs near the admin. One NPC opens its panel, several show an ID list.
    [ChatCommand("npc_gui")]
    private void CmdNpcGui(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }

        int chosen;
        if (args.Length > 0 && int.TryParse(args[0], out chosen))
        {
            OpenGuiById(player, chosen);
            return;
        }

        var radius = ThornConfig.ClampRadius(_config.GuiRadius);
        var ids = NpcsNear(player, radius);
        if (ids.Count == 0)
        {
            player.ChatMessage(Msg(player, "NoNpcInRadius", radius));
            return;
        }
        if (ids.Count == 1)
        {
            OpenGui(player, ids[0]);
            return;
        }
        OpenList(player, ids, radius);
    }

    // Clickable list of all NPCs within the radius. Each button opens that NPC's panel.
    private void OpenList(BasePlayer player, IReadOnlyList<int> ids, float radius)
    {
        CuiHelper.DestroyUi(player, GuiName);
        var c = new CuiElementContainer();
        c.Add(new CuiPanel
        {
            Image = { Color = "0.05 0.05 0.05 0.95" },
            RectTransform = { AnchorMin = "0.35 0.25", AnchorMax = "0.65 0.75" },
            CursorEnabled = true
        }, "Overlay", GuiName);

        AddText(c, "title", Msg(player, "GuiListTitle", radius), "0.05 0.90", "0.95 0.97", 16, TextAnchor.MiddleLeft);

        const int maxRows = 8;
        var shown = ids.Take(maxRows).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var id = shown[i];
            var stored = FindStored(id);
            var label = $"ID {id}  ·  {(string.IsNullOrEmpty(stored?.Name) ? Msg(player, "GuiNoName") : stored.Name)}";
            var top = 0.86f - i * 0.075f;
            var bottom = top - 0.065f;
            AddButton(c, "npc" + id, label, "thornnpc.gui select " + id + " 0",
                "0.05 " + Inv(bottom), "0.95 " + Inv(top));
        }
        if (ids.Count > maxRows)
        {
            AddText(c, "more", Msg(player, "GuiMore", ids.Count - maxRows), "0.05 0.17", "0.95 0.22", 12, TextAnchor.MiddleLeft);
        }

        AddButton(c, "close", Msg(player, "GuiClose"), "thornnpc.gui close 0 0", "0.35 0.05", "0.65 0.13");
        CuiHelper.AddUi(player, c);
    }

    // A random line from a message list (null when the list is empty).
    private static string RandomLine(List<string> lines) =>
        lines == null || lines.Count == 0 ? null : lines[UnityEngine.Random.Range(0, lines.Count)];

    // Chat line spoken by the NPC, prefixed with its name.
    private string NpcTalk(int id, string text)
    {
        var name = FindStored(id)?.Name;
        return string.IsNullOrEmpty(name) ? text : name + ": " + text;
    }

    private static string Inv(float value) => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    // Strips control characters and rich-text brackets (no <color>/<size> tags can be injected), then cuts to maxLength.
    private static string SanitizeText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) continue;
            sb.Append(ch == '<' || ch == '>' ? ' ' : ch);
        }
        var result = sb.ToString().Trim();
        return result.Length > maxLength ? result.Substring(0, maxLength) : result;
    }

    // NPCs within the radius of the player, nearest first. Uses the live position, because a patrolling NPC
    // can be far from its spawn point. Without a live figure the spawn point counts.
    private IReadOnlyList<int> NpcsNear(BasePlayer player, float radius)
    {
        var positions = _registry.All.Select(rec =>
        {
            ThornNpcBehaviour live;
            var at = _live.TryGetValue(rec.Id, out live) && live.Player != null
                ? ToPos(live.Player.transform.position)
                : rec.Spawn;
            return new NpcRecord(rec.Id, at);
        }).ToList();
        return NpcSelection.Within(positions, ToPos(player.transform.position), radius);
    }

    // /npc_gui <id>: opens the panel for a chosen NPC, still limited to the radius.
    private void OpenGuiById(BasePlayer player, int id)
    {
        var radius = ThornConfig.ClampRadius(_config.GuiRadius);
        var ids = NpcsNear(player, radius);
        if (!ids.Contains(id))
        {
            player.ChatMessage(Msg(player, "NpcNotInRadius", id, radius));
            return;
        }
        OpenGui(player, id);
    }

    private const string GuiName = "ThornNPC_Gui";
    private const string GuiFont = "robotocondensed-regular.ttf";
    private const string CardName = "ThornNPC_Card";

    [PluginReference] private Plugin ImageLibrary;

    private void OpenGui(BasePlayer player, int id)
    {
        CuiHelper.DestroyUi(player, GuiName);
        OpenIdCard(player, id);
        var c = new CuiElementContainer();
        c.Add(new CuiPanel
        {
            Image = { Color = "0.05 0.05 0.05 0.95" },
            RectTransform = { AnchorMin = "0.3 0.22", AnchorMax = "0.7 0.78" },
            CursorEnabled = true
        }, "Overlay", GuiName);

        var stored = FindStored(id);
        var name = stored?.Name ?? "";

        AddText(c, "title", Msg(player, "GuiSettingsTitle"), "0.05 0.91", "0.95 0.98", 18, TextAnchor.MiddleLeft);
        AddText(c, "id", Msg(player, "GuiId", id), "0.05 0.85", "0.95 0.90", 15, TextAnchor.MiddleLeft);
        AddText(c, "prot", Msg(player, "GuiProtection"), "0.05 0.80", "0.95 0.84", 12, TextAnchor.MiddleLeft);

        AddText(c, "namelabel", Msg(player, "GuiName"), "0.05 0.72", "0.25 0.78", 14, TextAnchor.MiddleLeft);
        AddInput(c, "name", name, "thornnpc.gui name " + id + " ", "0.27 0.72", "0.95 0.78");

        AddText(c, "notelabel", Msg(player, "GuiNote"), "0.05 0.63", "0.25 0.69", 14, TextAnchor.MiddleLeft);
        AddInput(c, "note", ResolveNoteText(id), "thornnpc.gui note " + id + " ", "0.27 0.63", "0.95 0.69", 200);

        var penaltyMin = ResolvePenaltySeconds(id) / 60;
        AddText(c, "pen", Msg(player, "GuiPenalty", penaltyMin), "0.05 0.54", "0.55 0.61", 14, TextAnchor.MiddleLeft);
        AddButton(c, "pen-", Msg(player, "GuiMinus1Min"), "thornnpc.gui penalty " + id + " -1", "0.60 0.54", "0.72 0.61");
        AddButton(c, "pen+", Msg(player, "GuiPlus1Min"), "thornnpc.gui penalty " + id + " 1", "0.75 0.54", "0.87 0.61");

        var radiusM = ResolveZoneRadius(id);
        AddText(c, "zone", Msg(player, "GuiZone", radiusM), "0.05 0.43", "0.55 0.50", 14, TextAnchor.MiddleLeft);
        AddButton(c, "zone-", Msg(player, "GuiMinus1m"), "thornnpc.gui zone " + id + " -1", "0.60 0.43", "0.72 0.50");
        AddButton(c, "zone+", Msg(player, "GuiPlus1m"), "thornnpc.gui zone " + id + " 1", "0.75 0.43", "0.87 0.50");

        var outfitCount = stored?.Outfit?.Count ?? 0;
        AddText(c, "outfit", Msg(player, "GuiOutfit", outfitCount), "0.05 0.31", "0.55 0.39", 14, TextAnchor.MiddleLeft);
        AddButton(c, "outfit", Msg(player, "GuiTakeOutfit"), "thornnpc.gui outfit " + id, "0.60 0.31", "0.95 0.39");

        AddButton(c, "close", Msg(player, "GuiClose"), "thornnpc.gui close 0 0", "0.35 0.05", "0.65 0.14");
        CuiHelper.AddUi(player, c);
    }

    // Same construction as WikiHelp / Shop: CuiLabel and CuiButton helpers with Font AND Color set.
    // Text without a Color made the client fail in AddUI.
    // ID card: strip above the settings panel with outfit icons, name, ID and shop categories.
    private void OpenIdCard(BasePlayer player, int id)
    {
        CuiHelper.DestroyUi(player, CardName);
        var stored = FindStored(id);
        var c = new CuiElementContainer();
        c.Add(new CuiPanel
        {
            Image = { Color = "0.08 0.08 0.08 0.95" },
            RectTransform = { AnchorMin = "0.3 0.80", AnchorMax = "0.7 0.93" },
            CursorEnabled = true
        }, "Overlay", CardName);

        // Outfit icons (up to six), as far as ImageLibrary has them.
        var outfit = stored?.Outfit ?? new List<OutfitItem>();
        for (var i = 0; i < Math.Min(6, outfit.Count); i++)
        {
            var image = ImageLibrary == null ? null : ImageLibrary.Call("GetImage", outfit[i].Shortname, outfit[i].SkinId) as string;
            if (string.IsNullOrEmpty(image)) continue;
            var x1 = 0.02f + i * 0.09f;
            c.Add(new CuiElement
            {
                Name = CardName + "_icon" + i,
                Parent = CardName,
                Components =
                {
                    new CuiRawImageComponent { Png = image, Color = "1 1 1 1" },
                    new CuiRectTransformComponent { AnchorMin = Inv(x1) + " 0.12", AnchorMax = Inv(x1 + 0.08f) + " 0.88" }
                }
            });
        }

        var name = string.IsNullOrEmpty(stored?.Name) ? Msg(player, "GuiNoName") : stored.Name;
        string shopText;
        try
        {
            var cfg = ReadShopConfig();
            shopText = IsShopLinked(cfg, id) ? ShopCategoriesText(player, cfg, id) : Msg(player, "GuiNotLinked");
        }
        catch (Exception)
        {
            shopText = Msg(player, "GuiShopUnreadable");
        }

        CardLabel(c, $"ID {id}  ·  {name}", "0.58 0.52", "0.98 0.90", 15, TextAnchor.MiddleLeft);
        CardLabel(c, Msg(player, "GuiIdCardShop", shopText), "0.58 0.10", "0.98 0.46", 11, TextAnchor.UpperLeft);
        CuiHelper.AddUi(player, c);
    }

    private static void CardLabel(CuiElementContainer c, string text, string min, string max, int size, TextAnchor align)
    {
        c.Add(new CuiLabel
        {
            Text = { Text = text, FontSize = size, Font = GuiFont, Align = align, Color = "1 1 1 1" },
            RectTransform = { AnchorMin = min, AnchorMax = max }
        }, CardName);
    }

    private static void AddText(CuiElementContainer c, string name, string text, string min, string max, int size, TextAnchor align)
    {
        c.Add(new CuiLabel
        {
            Text = { Text = text, FontSize = size, Font = GuiFont, Align = align, Color = "1 1 1 1" },
            RectTransform = { AnchorMin = min, AnchorMax = max }
        }, GuiName);
    }

    private static void AddButton(CuiElementContainer c, string name, string label, string command, string min, string max)
    {
        c.Add(new CuiButton
        {
            Button = { Command = command, Color = "0.2 0.45 0.25 0.9" },
            Text = { Text = label, FontSize = 14, Font = GuiFont, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
            RectTransform = { AnchorMin = min, AnchorMax = max }
        }, GuiName);
    }

    // Input field: on Enter the client sends Command + text (same pattern as Battlepass/EventsManager).
    private static void AddInput(CuiElementContainer c, string name, string value, string command, string min, string max, int maxChars = 32)
    {
        c.Add(new CuiElement
        {
            Name = GuiName + "_" + name,
            Parent = GuiName,
            Components =
            {
                new CuiInputFieldComponent
                {
                    Text = value,
                    FontSize = 14,
                    Font = GuiFont,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 1 1 1",
                    Command = command,
                    CharsLimit = maxChars,
                    // Without this, keystrokes also reach the game (movement, chat, binds) while typing.
                    NeedsKeyboard = true
                },
                new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max }
            }
        });
    }

    // Button actions. Each one checks the permission and the radius again.
    [ConsoleCommand("thornnpc.gui")]
    private void CmdGuiAction(ConsoleSystem.Arg arg)
    {
        var player = arg.Player();
        if (player == null) return;
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin)) return;

        var action = arg.GetString(0, "");
        if (action == "close")
        {
            CuiHelper.DestroyUi(player, GuiName);
            CuiHelper.DestroyUi(player, CardName);
            return;
        }

        var id = arg.GetInt(1, 0);
        var step = arg.GetInt(2, 0);
        var radius = ThornConfig.ClampRadius(_config.GuiRadius);
        if (!NpcsNear(player, radius).Contains(id))
        {
            CuiHelper.DestroyUi(player, GuiName);
            player.ChatMessage(Msg(player, "NpcNotInRadius", id, radius));
            return;
        }

        if (action == "select")
        {
            OpenGui(player, id);
            return;
        }
        if (action == "note")
        {
            var args = arg.Args;
            var newNote = args == null ? "" : string.Join(" ", args.Skip(2).Select(a => a.ToString())).Trim();
            var cleanNote = SanitizeText(newNote, 200);
            GetOrCreateSettings(id).NoteText = cleanNote.Length > 0 ? cleanNote : null;
            SaveConfig();
            OpenGui(player, id);
            return;
        }
        if (action == "name")
        {
            var args = arg.Args;
            var newName = args == null ? "" : string.Join(" ", args.Skip(2).Select(a => a.ToString())).Trim();
            var cleanName = SanitizeText(newName, 32);
            if (cleanName.Length > 0) RenameNpc(id, cleanName);
            OpenGui(player, id);
            return;
        }
        if (action == "outfit")
        {
            CaptureOutfit(player, id);
            OpenGui(player, id);
            return;
        }

        var settings = GetOrCreateSettings(id);
        if (action == "penalty")
        {
            var seconds = ResolvePenaltySeconds(id) + step * 60;
            settings.PenaltySeconds = (int)ThornConfig.ClampPenalty(TimeSpan.FromSeconds(Math.Max(10, seconds))).TotalSeconds;
        }
        else if (action == "zone")
        {
            settings.ZoneRadius = Math.Max(0f, ResolveZoneRadius(id) + step);
        }
        SaveConfig();
        OpenGui(player, id);
    }

    #endregion

    #region Shop links (/npc_shop)

    private const string ShopGuiName = "ThornNPC_ShopGui";
    private const string ShopSection = "NPC Shops (NPC ID - shop categories)";
    private const int ShopRowsPerPage = 8;
    private readonly Dictionary<ulong, int> _shopPage = new Dictionary<ulong, int>();

    // Shop category titles that are enabled. Read from the Shop data file on every call,
    // so changes made in the Shop are visible the next time the list is opened.
    private List<string> ReadShopCategoryTitles()
    {
        var result = new List<string>();
        try
        {
            var data = Interface.Oxide.DataFileSystem.GetFile("Shop/Shops/Default").ReadObject<JObject>();
            var categories = data?["Shop Categories"] as JArray;
            if (categories == null) return result;
            foreach (var category in categories)
            {
                if (category["Enabled"]?.Value<bool>() != true) continue;
                var title = category["Title"]?.Value<string>();
                if (!string.IsNullOrEmpty(title)) result.Add(title);
            }
        }
        catch (Exception e)
        {
            PrintError("Could not read Shop categories: " + e.GetType().Name);
        }
        return result;
    }

    private static string ShopConfigPath() => Path.Combine(Interface.Oxide.ConfigDirectory, "Shop.json");

    // Missing or unreadable Shop.json counts as "no links" instead of throwing into the chat command.
    private static JObject ReadShopConfig()
    {
        try
        {
            var path = ShopConfigPath();
            return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        }
        catch (Exception)
        {
            return new JObject();
        }
    }

    // The Shop key for a ThornNPC is its fake user ID.
    private static string ShopKeyFor(int id) => UserIdFor(id).ToString();

    private static bool IsShopLinked(JObject shopConfig, int id) =>
        (shopConfig[ShopSection] as JObject)?[ShopKeyFor(id)] != null;

    private string ShopCategoriesText(BasePlayer player, JObject shopConfig, int id)
    {
        var entry = (shopConfig[ShopSection] as JObject)?[ShopKeyFor(id)] as JObject;
        var cats = entry?["Categories (Titles) [* - all]"] as JArray;
        if (cats == null || cats.Count == 0) return Msg(player, "GuiNoCategories");
        return string.Join(", ", cats.Select(c => c.Value<string>()));
    }

    [ChatCommand("npc_shop")]
    private void CmdNpcShop(BasePlayer player, string command, string[] args)
    {
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
        {
            player.ChatMessage(Msg(player, "NoPermission"));
            return;
        }
        _shopPage[player.userID] = 0;
        OpenShopList(player);
    }

    private void OpenShopList(BasePlayer player)
    {
        CuiHelper.DestroyUi(player, ShopGuiName);
        var shopConfig = ReadShopConfig();
        var npcs = _data.Npcs.OrderBy(n => n.Id).ToList();
        var pages = Math.Max(1, (int)Math.Ceiling(npcs.Count / (double)ShopRowsPerPage));
        int page;
        _shopPage.TryGetValue(player.userID, out page);
        page = Math.Max(0, Math.Min(page, pages - 1));
        _shopPage[player.userID] = page;

        var c = new CuiElementContainer();
        c.Add(new CuiPanel
        {
            Image = { Color = "0.05 0.05 0.05 0.95" },
            RectTransform = { AnchorMin = "0.12 0.12", AnchorMax = "0.88 0.88" },
            CursorEnabled = true
        }, "Overlay", ShopGuiName);

        ShopLabel(c, Msg(player, "GuiShopTitle"), "0.03 0.92", "0.97 0.98", 18, TextAnchor.MiddleLeft);
        ShopLabel(c, Msg(player, "GuiPage", page + 1, pages, npcs.Count), "0.03 0.87", "0.97 0.91", 12, TextAnchor.MiddleLeft);

        if (npcs.Count == 0)
        {
            ShopLabel(c, Msg(player, "GuiNoNpcs"), "0.03 0.6", "0.97 0.7", 14, TextAnchor.MiddleLeft);
        }

        var rows = npcs.Skip(page * ShopRowsPerPage).Take(ShopRowsPerPage).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var npc = rows[i];
            var top = 0.82f - i * 0.085f;
            var bottom = top - 0.07f;
            var linked = IsShopLinked(shopConfig, npc.Id);
            var name = string.IsNullOrEmpty(npc.Name) ? Msg(player, "GuiNoName") : npc.Name;

            ShopLabel(c, $"ID {npc.Id}  ·  {name}", $"0.03 {Inv(bottom)}", $"0.40 {Inv(top)}", 14, TextAnchor.MiddleLeft);
            ShopLabel(c, linked ? ShopCategoriesText(player, shopConfig, npc.Id) : Msg(player, "GuiNotLinked"),
                $"0.41 {Inv(bottom)}", $"0.70 {Inv(top)}", 12, TextAnchor.MiddleLeft);
            ShopButton(c, Msg(player, linked ? "GuiShopOff" : "GuiShopOn"), "thornnpc.shop toggle " + npc.Id,
                $"0.71 {Inv(bottom)}", $"0.80 {Inv(top)}");
            ShopButton(c, Msg(player, "GuiCategories"), "thornnpc.shop cats " + npc.Id,
                $"0.81 {Inv(bottom)}", $"0.97 {Inv(top)}");
        }

        ShopButton(c, "<", "thornnpc.shop page -1", "0.03 0.03", "0.10 0.09");
        ShopButton(c, ">", "thornnpc.shop page 1", "0.11 0.03", "0.18 0.09");
        ShopButton(c, Msg(player, "GuiClose"), "thornnpc.shop close 0", "0.80 0.03", "0.97 0.09");

        CuiHelper.AddUi(player, c);
    }

    private static void ShopLabel(CuiElementContainer c, string text, string min, string max, int size, TextAnchor align)
    {
        c.Add(new CuiLabel
        {
            Text = { Text = text, FontSize = size, Font = GuiFont, Align = align, Color = "1 1 1 1" },
            RectTransform = { AnchorMin = min, AnchorMax = max }
        }, ShopGuiName);
    }

    private static void ShopButton(CuiElementContainer c, string label, string command, string min, string max)
    {
        c.Add(new CuiButton
        {
            Button = { Command = command, Color = "0.2 0.45 0.25 0.9" },
            Text = { Text = label, FontSize = 13, Font = GuiFont, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
            RectTransform = { AnchorMin = min, AnchorMax = max }
        }, ShopGuiName);
    }

    // Category selection in progress per admin: which NPC, and which category titles are ticked.
    private class ShopEdit
    {
        public int NpcId;
        public HashSet<string> Titles = new HashSet<string>();
    }

    private readonly Dictionary<ulong, ShopEdit> _shopEdit = new Dictionary<ulong, ShopEdit>();
    private const int ShopCategoryColumns = 2;
    private const int ShopCategoryRows = 12;

    private void OpenShopCategories(BasePlayer player, int id, bool preselect)
    {
        var shopConfig = ReadShopConfig();
        var edit = new ShopEdit { NpcId = id };
        if (preselect)
        {
            var entry = (shopConfig[ShopSection] as JObject)?[ShopKeyFor(id)] as JObject;
            var cats = entry?["Categories (Titles) [* - all]"] as JArray;
            if (cats != null) foreach (var c in cats) edit.Titles.Add(c.Value<string>());
        }
        _shopEdit[player.userID] = edit;
        RenderShopCategories(player, edit);
    }

    // Reads the category list again on each render, so Shop changes show up immediately.
    private void RenderShopCategories(BasePlayer player, ShopEdit edit)
    {
        CuiHelper.DestroyUi(player, ShopGuiName);
        var titles = ReadShopCategoryTitles().Take(ShopCategoryColumns * ShopCategoryRows).ToList();

        var c = new CuiElementContainer();
        c.Add(new CuiPanel
        {
            Image = { Color = "0.05 0.05 0.05 0.95" },
            RectTransform = { AnchorMin = "0.12 0.12", AnchorMax = "0.88 0.88" },
            CursorEnabled = true
        }, "Overlay", ShopGuiName);

        ShopLabel(c, Msg(player, "GuiCatTitle", edit.NpcId), "0.03 0.92", "0.97 0.98", 18, TextAnchor.MiddleLeft);
        ShopLabel(c, Msg(player, "GuiCatHint"), "0.03 0.87", "0.97 0.91", 12, TextAnchor.MiddleLeft);

        const float colWidth = 0.46f;
        var rowHeight = 0.9f / (ShopCategoryRows + 0.5f) * 0.92f;
        for (var i = 0; i < titles.Count; i++)
        {
            var col = i / ShopCategoryRows;
            var row = i % ShopCategoryRows;
            var x1 = 0.03f + col * (colWidth + 0.02f);
            var x2 = x1 + colWidth;
            var top = 0.85f - row * rowHeight;
            var bottom = top - rowHeight * 0.85f;
            var on = edit.Titles.Contains(titles[i]);
            ShopButton(c, (on ? "[x]  " : "[ ]  ") + titles[i], "thornnpc.shop pick " + i,
                $"{Inv(x1)} {Inv(bottom)}", $"{Inv(x2)} {Inv(top)}");
        }
        if (titles.Count == 0)
        {
            ShopLabel(c, Msg(player, "GuiNoEnabledCats"), "0.03 0.6", "0.97 0.7", 14, TextAnchor.MiddleLeft);
        }

        ShopButton(c, Msg(player, "GuiSave"), "thornnpc.shop save " + edit.NpcId, "0.55 0.03", "0.78 0.09");
        ShopButton(c, Msg(player, "GuiBack"), "thornnpc.shop back 0", "0.80 0.03", "0.97 0.09");
        CuiHelper.AddUi(player, c);
    }

    // Button actions of the shop list and the category panel.
    [ConsoleCommand("thornnpc.shop")]
    private void CmdShopAction(ConsoleSystem.Arg arg)
    {
        var player = arg.Player();
        if (player == null) return;
        if (!permission.UserHasPermission(player.UserIDString, PermAdmin)) return;

        var action = arg.GetString(0, "");
        switch (action)
        {
            case "close":
                _shopEdit.Remove(player.userID);
                CuiHelper.DestroyUi(player, ShopGuiName);
                return;
            case "page":
                int page;
                _shopPage.TryGetValue(player.userID, out page);
                _shopPage[player.userID] = Math.Max(0, page + arg.GetInt(1, 0));
                OpenShopList(player);
                return;
            case "toggle":
                ToggleShopLink(player, arg.GetInt(1, 0));
                return;
            case "cats":
                OpenShopCategories(player, arg.GetInt(1, 0), preselect: true);
                return;
            case "pick":
                PickShopCategory(player, arg.GetInt(1, -1));
                return;
            case "save":
                SaveShopSelection(player, arg.GetInt(1, 0));
                return;
            case "back":
                _shopEdit.Remove(player.userID);
                OpenShopList(player);
                return;
        }
    }

    private void ToggleShopLink(BasePlayer player, int id)
    {
        if (FindStored(id) == null) return;
        if (IsShopLinked(ReadShopConfig(), id))
        {
            WriteShopLinks(id, null);
            player.ChatMessage(Msg(player, "ShopUnlinked", id));
            OpenShopList(player);
        }
        else
        {
            OpenShopCategories(player, id, preselect: false);
        }
    }

    private void PickShopCategory(BasePlayer player, int index)
    {
        ShopEdit edit;
        if (!_shopEdit.TryGetValue(player.userID, out edit)) return;
        var titles = ReadShopCategoryTitles();
        if (index < 0 || index >= titles.Count) return;
        var title = titles[index];
        if (!edit.Titles.Remove(title)) edit.Titles.Add(title);
        RenderShopCategories(player, edit);
    }

    private void SaveShopSelection(BasePlayer player, int id)
    {
        ShopEdit edit;
        if (!_shopEdit.TryGetValue(player.userID, out edit) || edit.NpcId != id) return;
        if (edit.Titles.Count == 0)
        {
            player.ChatMessage(Msg(player, "ShopPickOne"));
            return;
        }
        WriteShopLinks(id, edit.Titles.ToList());
        _shopEdit.Remove(player.userID);
        player.ChatMessage(Msg(player, "ShopLinked", id, edit.Titles.Count));
        OpenShopList(player);
    }

    // Writes one NPC's link into the RUNNING Shop, using the steps the Shop uses for its own
    // /shop.setnpc command: update its config entry, load the shop data, then let the Shop save.
    // Writing Shop.json directly was overwritten by the Shop (MISMATCH in the test), so the
    // Shop's own copy is changed instead. categories == null removes the link.
    // Links of ThornNPCs that no longer exist are removed in the same step.
    private void WriteShopLinks(int id, List<string> categories)
    {
        // The Shop keeps its config in a STATIC field (_config), so Static must be searched too.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var shop = Interface.Oxide.RootPluginManager.GetPlugin("Shop");
            if (shop == null || shop.Object == null)
            {
                PrintError("Shop is not loaded, no links written.");
                return;
            }
            var shopObj = shop.Object;
            var type = shopObj.GetType();

            var cfg = type.GetField("_config", flags)?.GetValue(shopObj);
            var npcsField = cfg?.GetType().GetField("NPCs", flags);
            var dict = npcsField?.GetValue(cfg) as System.Collections.IDictionary;
            var saveMethod = type.GetMethod("SaveConfig", flags);
            if (dict == null || saveMethod == null)
            {
                PrintError("Shop internals not found (Shop version changed?). No links written.");
                return;
            }

            var key = ShopKeyFor(id);
            if (categories == null)
            {
                dict.Remove(key);
                ResetNpcShopData(shopObj, type, flags, key);
            }
            else
            {
                var entryType = dict.GetType().GetGenericArguments()[1];
                var exists = dict.Contains(key);
                var entry = exists ? dict[key] : Activator.CreateInstance(entryType);
                SetShopField(entry, "BotID", key);
                SetShopField(entry, "Categories", categories);
                if (!exists)
                {
                    SetShopField(entry, "Permission", "");
                    SetShopField(entry, "AllowFavorites", false);
                }
                var allowFavorites = GetShopField(entry, "AllowFavorites");
                // The Shop reuses an existing NPC item file as is, so the old categories would stay.
                // Remove the file and its cache entry, so LoadShopFile builds it from the new categories.
                ResetNpcShopData(shopObj, type, flags, key);
                var loadShopFile = type.GetMethod("LoadShopFile", flags);
                var data = loadShopFile?.Invoke(shopObj, new object[] { Path.Combine("NPC", key), categories, allowFavorites });
                SetShopField(entry, "Data", data);
                dict[key] = entry;
            }

            // Drop links of ThornNPCs that no longer exist.
            var valid = new HashSet<string>(_registry.All.Select(n => ShopKeyFor(n.Id)));
            foreach (var k in dict.Keys.Cast<object>().Select(x => x.ToString()).ToList())
            {
                ulong uid;
                if (!ulong.TryParse(k, out uid)) continue;
                if (uid < UserIdBase || uid >= UserIdBase + 1000000UL) continue;
                if (!valid.Contains(k)) dict.Remove(k);
            }

            saveMethod.Invoke(shopObj, null);
            PrintWarning($"Shop link {(categories == null ? "removed" : "set")} for NPC {id} in the running Shop.");
            timer.Once(10f, () => VerifyShopLink(id, categories != null));
        }
        catch (Exception e)
        {
            // Message only, no stack trace or file paths in the server console.
            PrintError("Could not write Shop links: " + e.GetType().Name);
        }
    }

    // Deletes the Shop's item file for one NPC and its in-memory cache entry (the Shop's _shops).
    private void ResetNpcShopData(object shopObj, Type type, BindingFlags flags, string key)
    {
        var fileKey = Path.Combine("NPC", key);
        Interface.Oxide.DataFileSystem.DeleteDataFile(Path.Combine("Shop", "Shops", fileKey));
        var cache = type.GetField("_shops", flags)?.GetValue(shopObj) as System.Collections.IDictionary;
        if (cache != null && cache.Contains(fileKey)) cache.Remove(fileKey);
    }

    private static void SetShopField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) throw new MissingFieldException(target.GetType().Name, name);
        field.SetValue(target, value);
    }

    private static object GetShopField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) throw new MissingFieldException(target.GetType().Name, name);
        return field.GetValue(target);
    }

    private void VerifyShopLink(int id, bool expectLink)
    {
        try
        {
            var linked = IsShopLinked(ReadShopConfig(), id);
            if (linked == expectLink)
                PrintWarning($"Shop link check NPC {id}: OK (linked: {linked}).");
            else
                PrintError($"Shop link check NPC {id}: MISMATCH (expected linked: {expectLink}, found: {linked}). Shop.json was overwritten after the write.");
        }
        catch (Exception e)
        {
            PrintError("Shop link check failed: " + e.Message);
        }
    }

    #endregion

    #region Harmony: fake NPC players count as bots (as in HumanNPC)

    [AutoPatch]
    [HarmonyPatch(typeof(BasePlayer), "get_IsBot")]
    internal class PatchIsBot
    {
        [HarmonyPrefix]
        internal static bool Prefix(BasePlayer __instance, ref bool __result)
        {
            if (__instance == null || __instance.userID == null || _ins == null) return true;
            if (__instance.GetComponent<ThornNpcBehaviour>() == null) return true;
            __result = true;
            return false;
        }
    }

    #endregion
}
}
