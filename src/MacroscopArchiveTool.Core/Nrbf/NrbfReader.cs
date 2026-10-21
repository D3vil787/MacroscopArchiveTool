using System.Text;

namespace MacroscopArchiveTool.Core.Nrbf;

/// <summary>A deserialized class instance: type name and member values. Nothing is instantiated or executed.</summary>
public sealed class NrbfObject
{
    public int Id { get; init; }
    public string ClassName { get; init; } = "";
    public string? LibraryName { get; init; }
    /// <summary>Member name → value: primitive, string, Guid, <see cref="NrbfObject"/>, <see cref="NrbfArray"/>, <see cref="NrbfRef"/> or null.</summary>
    public List<KeyValuePair<string, object?>> Members { get; } = new();

    public object? this[string name] => Members.FirstOrDefault(m => m.Key == name).Value;
    public override string ToString() => $"#{Id} {ClassName}";
}

public sealed class NrbfArray
{
    public int Id { get; init; }
    public List<object?> Items { get; } = new();
}

/// <summary>Reference to an object defined elsewhere in the stream (MemberReference record).</summary>
public readonly record struct NrbfRef(int Id);

/// <summary>Result of reading one or more NRBF streams.</summary>
public sealed class NrbfGraph
{
    public Dictionary<int, object> ById { get; } = new();
    public List<object?> Roots { get; } = new();

    public object? Resolve(object? v) => v is NrbfRef r ? ById.GetValueOrDefault(r.Id) : v;

    public IEnumerable<NrbfObject> AllObjects() => ById.Values.OfType<NrbfObject>();
}

/// <summary>
/// Passive reader of the .NET Remoting Binary Format ([MS-NRBF], the BinaryFormatter wire format).
/// It only parses records into plain data — no types are loaded and no code from the file runs,
/// so it is safe to use on untrusted input (unlike BinaryFormatter itself).
/// </summary>
public sealed class NrbfReader
{
    private enum Rec : byte
    {
        Header = 0, ClassWithId = 1, SystemClassWithMembers = 2, ClassWithMembers = 3,
        SystemClassWithMembersAndTypes = 4, ClassWithMembersAndTypes = 5, BinaryObjectString = 6,
        BinaryArray = 7, MemberPrimitiveTyped = 8, MemberReference = 9, ObjectNull = 10, MessageEnd = 11,
        BinaryLibrary = 12, ObjectNullMultiple256 = 13, ObjectNullMultiple = 14,
        ArraySinglePrimitive = 15, ArraySingleObject = 16, ArraySingleString = 17,
    }

    private enum BinType : byte { Primitive = 0, String = 1, Object = 2, SystemClass = 3, Class = 4, ObjectArray = 5, StringArray = 6, PrimitiveArray = 7 }

    private sealed class ClassMeta
    {
        public string Name = "";
        public string[] Members = Array.Empty<string>();
        public BinType[]? Types;              // null = untyped (ClassWithMembers)
        public byte[]? PrimTypes;             // primitive type per member, when Types[i] is Primitive
        public int LibraryId = -1;
    }

    private sealed class NullRun { public int Count; }

    private readonly BinaryReader _r;
    private readonly Dictionary<int, ClassMeta> _meta = new();
    private readonly Dictionary<int, string> _libs = new();
    private readonly NrbfGraph _g;
    private const int MaxElements = 50_000_000;

    private NrbfReader(Stream s, NrbfGraph g) { _r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true); _g = g; }

    /// <summary>
    /// Finds every NRBF stream inside <paramref name="data"/> (the payload may be wrapped in a custom header)
    /// and reads them into one graph. Returns the offsets where streams started.
    /// </summary>
    public static (NrbfGraph Graph, List<int> Offsets) ReadAll(byte[] data)
    {
        var g = new NrbfGraph();
        var offsets = new List<int>();
        int pos = 0;
        while ((pos = FindHeader(data, pos)) >= 0)
        {
            using var ms = new MemoryStream(data, pos, data.Length - pos, writable: false);
            var reader = new NrbfReader(ms, g);
            try
            {
                var root = reader.ReadStream();
                g.Roots.Add(root);
                offsets.Add(pos);
                pos += (int)ms.Position;
            }
            catch (Exception) when (offsets.Count > 0)
            {
                pos++; // a later false-positive header match; keep what was already read
            }
        }
        if (offsets.Count == 0) throw new InvalidDataException("В файле не найден поток формата NRBF.");
        return (g, offsets);
    }

    /// <summary>SerializedStreamHeader: 0x00, rootId, headerId, majorVersion=1, minorVersion=0.</summary>
    internal static int FindHeader(byte[] d, int start)
    {
        for (int i = start; i + 17 <= d.Length; i++)
        {
            if (d[i] != 0) continue;
            if (d[i + 9] == 1 && d[i + 10] == 0 && d[i + 11] == 0 && d[i + 12] == 0 &&
                d[i + 13] == 0 && d[i + 14] == 0 && d[i + 15] == 0 && d[i + 16] == 0 &&
                (i + 17 < d.Length && IsRecordStart(d[i + 17])))
                return i;
        }
        return -1;
    }

    private static bool IsRecordStart(byte b) => b <= 17 && b != 0;

    private object? ReadStream()
    {
        if ((Rec)_r.ReadByte() != Rec.Header) throw new InvalidDataException("Нет заголовка NRBF");
        int rootId = _r.ReadInt32();
        _r.ReadInt32(); _r.ReadInt32(); _r.ReadInt32();
        while (true)
        {
            var v = ReadRecord(out var rec);
            if (rec == Rec.MessageEnd) break;
            _ = v;
        }
        return _g.ById.GetValueOrDefault(rootId);
    }

    /// <summary>Reads one record; BinaryLibrary records are consumed transparently.</summary>
    private object? ReadRecord(out Rec rec)
    {
        while (true)
        {
            rec = (Rec)_r.ReadByte();
            switch (rec)
            {
                case Rec.BinaryLibrary:
                    _libs[_r.ReadInt32()] = _r.ReadString();
                    continue;
                case Rec.MessageEnd:
                case Rec.ObjectNull:
                    return null;
                case Rec.ObjectNullMultiple256:
                    return new NullRun { Count = _r.ReadByte() };
                case Rec.ObjectNullMultiple:
                    return new NullRun { Count = _r.ReadInt32() };
                case Rec.MemberReference:
                    return new NrbfRef(_r.ReadInt32());
                case Rec.BinaryObjectString:
                {
                    int id = _r.ReadInt32();
                    var s = _r.ReadString();
                    _g.ById[id] = s;
                    return s;
                }
                case Rec.MemberPrimitiveTyped:
                    return ReadPrimitive(_r.ReadByte());
                case Rec.ClassWithMembersAndTypes:
                case Rec.SystemClassWithMembersAndTypes:
                case Rec.ClassWithMembers:
                case Rec.SystemClassWithMembers:
                    return ReadClassDefinition(rec);
                case Rec.ClassWithId:
                {
                    int id = _r.ReadInt32();
                    int metaId = _r.ReadInt32();
                    if (!_meta.TryGetValue(metaId, out var m)) throw new InvalidDataException($"Нет метаданных класса {metaId}");
                    return ReadClassValues(id, m);
                }
                case Rec.BinaryArray: return ReadBinaryArray();
                case Rec.ArraySinglePrimitive:
                {
                    int id = _r.ReadInt32(); int len = CheckLen(_r.ReadInt32()); byte pt = _r.ReadByte();
                    var a = new NrbfArray { Id = id };
                    _g.ById[id] = a;
                    if (pt == 2) a.Items.Add(_r.ReadBytes(len)); // byte[] kept as one blob
                    else for (int i = 0; i < len; i++) a.Items.Add(ReadPrimitive(pt));
                    return a;
                }
                case Rec.ArraySingleObject:
                case Rec.ArraySingleString:
                {
                    int id = _r.ReadInt32(); int len = CheckLen(_r.ReadInt32());
                    var a = new NrbfArray { Id = id };
                    _g.ById[id] = a;
                    ReadRecordElements(a, len);
                    return a;
                }
                default:
                    throw new InvalidDataException($"Неизвестный тип записи NRBF {(byte)rec} в позиции {_r.BaseStream.Position - 1}");
            }
        }
    }

    private object ReadClassDefinition(Rec rec)
    {
        int id = _r.ReadInt32();
        var m = new ClassMeta { Name = _r.ReadString() };
        int count = _r.ReadInt32();
        if (count < 0 || count > 100_000) throw new InvalidDataException("Неверное число членов класса");
        m.Members = new string[count];
        for (int i = 0; i < count; i++) m.Members[i] = _r.ReadString();

        if (rec is Rec.ClassWithMembersAndTypes or Rec.SystemClassWithMembersAndTypes)
        {
            m.Types = new BinType[count];
            m.PrimTypes = new byte[count];
            for (int i = 0; i < count; i++) m.Types[i] = (BinType)_r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                switch (m.Types[i])
                {
                    case BinType.Primitive:
                    case BinType.PrimitiveArray: m.PrimTypes[i] = _r.ReadByte(); break;
                    case BinType.SystemClass: _r.ReadString(); break;
                    case BinType.Class: _r.ReadString(); _r.ReadInt32(); break;
                }
            }
        }
        if (rec is Rec.ClassWithMembersAndTypes or Rec.ClassWithMembers) m.LibraryId = _r.ReadInt32();
        _meta[id] = m;
        return ReadClassValues(id, m);
    }

    private object ReadClassValues(int id, ClassMeta m)
    {
        var o = new NrbfObject { Id = id, ClassName = m.Name, LibraryName = _libs.GetValueOrDefault(m.LibraryId) };
        _g.ById[id] = o;
        for (int i = 0; i < m.Members.Length; i++)
        {
            object? v = m.Types != null && m.Types[i] == BinType.Primitive
                ? ReadPrimitive(m.PrimTypes![i])
                : ReadRecord(out _);
            o.Members.Add(new(m.Members[i], v));
        }
        // System.Guid is serialized as a class: collapse it into a real Guid value.
        if (m.Name == "System.Guid" && TryGuid(o, out var g))
        {
            var box = new GuidBox(g);
            _g.ById[id] = box;
            return box;
        }
        return o;
    }

    private object ReadBinaryArray()
    {
        int id = _r.ReadInt32();
        byte arrType = _r.ReadByte();
        int rank = _r.ReadInt32();
        if (rank is < 1 or > 32) throw new InvalidDataException("Неверная размерность массива");
        long total = 1;
        for (int i = 0; i < rank; i++) total *= CheckLen(_r.ReadInt32());
        if (arrType is 3 or 4 or 5) for (int i = 0; i < rank; i++) _r.ReadInt32(); // lower bounds
        var t = (BinType)_r.ReadByte();
        byte prim = 0;
        switch (t)
        {
            case BinType.Primitive:
            case BinType.PrimitiveArray: prim = _r.ReadByte(); break;
            case BinType.SystemClass: _r.ReadString(); break;
            case BinType.Class: _r.ReadString(); _r.ReadInt32(); break;
        }
        var a = new NrbfArray { Id = id };
        _g.ById[id] = a;
        int len = CheckLen(total);
        if (t == BinType.Primitive) for (int i = 0; i < len; i++) a.Items.Add(ReadPrimitive(prim));
        else ReadRecordElements(a, len);
        return a;
    }

    private void ReadRecordElements(NrbfArray a, int len)
    {
        while (a.Items.Count < len)
        {
            var v = ReadRecord(out _);
            if (v is NullRun run) { for (int k = 0; k < run.Count && a.Items.Count < len; k++) a.Items.Add(null); }
            else a.Items.Add(v);
        }
    }

    private object? ReadPrimitive(byte type) => type switch
    {
        1 => _r.ReadBoolean(),
        2 => _r.ReadByte(),
        3 => ReadUtf8Char(),
        5 => decimal.TryParse(_r.ReadString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m,
        6 => _r.ReadDouble(),
        7 => _r.ReadInt16(),
        8 => _r.ReadInt32(),
        9 => _r.ReadInt64(),
        10 => _r.ReadSByte(),
        11 => _r.ReadSingle(),
        12 => new TimeSpan(_r.ReadInt64()),
        13 => DateTime.FromBinary(_r.ReadInt64()),
        14 => _r.ReadUInt16(),
        15 => _r.ReadUInt32(),
        16 => _r.ReadUInt64(),
        17 => null,
        18 => _r.ReadString(),
        _ => throw new InvalidDataException($"Неизвестный примитивный тип {type}"),
    };

    private char ReadUtf8Char()
    {
        byte b = _r.ReadByte();
        int extra = b < 0x80 ? 0 : b < 0xE0 ? 1 : b < 0xF0 ? 2 : 3;
        var buf = new byte[extra + 1]; buf[0] = b;
        for (int i = 1; i <= extra; i++) buf[i] = _r.ReadByte();
        var s = Encoding.UTF8.GetString(buf);
        return s.Length > 0 ? s[0] : '\0';
    }

    private static int CheckLen(long n)
    {
        if (n < 0 || n > MaxElements) throw new InvalidDataException("Неверная длина массива");
        return (int)n;
    }

    private static bool TryGuid(NrbfObject o, out Guid g)
    {
        g = Guid.Empty;
        try
        {
            g = new Guid(Convert.ToInt32(o["_a"]), Convert.ToInt16(o["_b"]), Convert.ToInt16(o["_c"]),
                Convert.ToByte(o["_d"]), Convert.ToByte(o["_e"]), Convert.ToByte(o["_f"]), Convert.ToByte(o["_g"]),
                Convert.ToByte(o["_h"]), Convert.ToByte(o["_i"]), Convert.ToByte(o["_j"]), Convert.ToByte(o["_k"]));
            return true;
        }
        catch { return false; }
    }
}

/// <summary>A System.Guid read from the stream (stored in NrbfGraph.ById instead of the raw struct object).</summary>
public sealed record GuidBox(Guid Value);
