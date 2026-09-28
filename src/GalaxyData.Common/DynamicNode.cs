using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GalaxyData.Common;

using DynamicNodeArray = List<DynamicNode>;
using DynamicNodeObject = Dictionary<string, DynamicNode>;

[SuppressMessage("Design", "CA1036:Override methods on comparable types", Justification = "<Pending>")]
[SuppressMessage("Usage", "CA2231:Overload operator equals on overriding value type Equals", Justification = "<Pending>")]
public readonly struct DynamicNode : IEquatable<DynamicNode>, IComparable<DynamicNode>
{
   public static readonly DynamicNode Undefined = new(DynamicNodeType.Undefined);
   public static readonly DynamicNode Null = new(DynamicNodeType.Null);
   public static readonly DynamicNode True = new(true);
   public static readonly DynamicNode False = new(false);
   public static readonly DynamicNode One = new(1);
   public static readonly DynamicNode Zero = new(0L);

#pragma warning disable CA1051
   public readonly DynamicNodeType Type;
#pragma warning restore CA1051
   private readonly object? refValue;
   private readonly DynamicNodeValueType value;

   public DynamicNode this[string name] { get => GetProperty(name); set => SetProperty(name, value); }
   public DynamicNode this[int index] { get => GetIndex(index); set => SetIndex(index, value); }
   public int Count => refValue is DynamicNodeArray a ? a.Count : refValue is DynamicNodeObject o ? o.Count : 0;

   public DynamicNode(bool val) { Type = val ? DynamicNodeType.True : DynamicNodeType.False; }
   public DynamicNode(long val) { Type = DynamicNodeType.Integer; value = new(val); }
   public DynamicNode(double val) { Type = DynamicNodeType.Number; value = new(val); }
   public DynamicNode(decimal val) { Type = DynamicNodeType.Decimal; refValue = val; }
   public DynamicNode(Guid val) { Type = DynamicNodeType.Guid; refValue = val; }
   public DynamicNode(DateOnly val) : this(val.ToDateTime(default)) { }
   public DynamicNode(DateTime val) { Type = val.Kind == DateTimeKind.Utc ? DynamicNodeType.DateTimeUtc : DynamicNodeType.DateTimeLocal; value = new(val.Ticks); }
   public DynamicNode(DateTimeOffset val) { Type = DynamicNodeType.DateTimeOffset; refValue = val; }
   public DynamicNode(string? val) { Type = val == null ? DynamicNodeType.Null : DynamicNodeType.String; refValue = val; }
   public DynamicNode(byte[]? val) { Type = val == null ? DynamicNodeType.Null : DynamicNodeType.Binary; refValue = val; }
   public DynamicNode(DynamicNodeArray array) { Type = DynamicNodeType.Array; refValue = array; }
   public DynamicNode(DynamicNodeObject obj)
   {
      Type = DynamicNodeType.Object;
      refValue = obj == null || ReferenceEquals(obj.Comparer, StringComparer.OrdinalIgnoreCase)
         ? obj
         : new DynamicNodeObject(obj, StringComparer.OrdinalIgnoreCase);
   }
   public DynamicNode(IEnumerable<DynamicNode> list, bool asEnumerable = false)
   {
      if (asEnumerable) { Type = DynamicNodeType.Enumerable; refValue = list; }
      else { Type = DynamicNodeType.Array; refValue = new DynamicNodeArray(list); }
   }
   public DynamicNode(IEnumerable<KeyValuePair<string, DynamicNode>> obj) { Type = DynamicNodeType.Object; refValue = new DynamicNodeObject(obj, StringComparer.OrdinalIgnoreCase); }
   public DynamicNode(Func<DynamicNode[], DynamicNode> func) { Type = DynamicNodeType.Function; refValue = func; }
   public DynamicNode(DynamicNodeType type)
   {
      Type = type;
      if (type == DynamicNodeType.Array) { refValue = new DynamicNodeArray(); }
      else if (type == DynamicNodeType.Object) { refValue = new DynamicNodeObject(StringComparer.OrdinalIgnoreCase); }
   }

   public static DynamicNode NewObject() { return new(DynamicNodeType.Object); }
   public static DynamicNode NewArray() { return new(DynamicNodeType.Array); }
   public static DynamicNode FromString(string? s) { return new(s); }
   public static DynamicNode FromInt32(int i) { return new(i); }
   public static DynamicNode FromFlt32(float f) { return new(f); }

   #region Implicit

   public static implicit operator DynamicNode(bool val) => new(val);
   public static implicit operator DynamicNode(sbyte val) => new(val);
   public static implicit operator DynamicNode(byte val) => new(val);
   public static implicit operator DynamicNode(short val) => new(val);
   public static implicit operator DynamicNode(ushort val) => new(val);
   public static implicit operator DynamicNode(int val) => new(val);
   public static implicit operator DynamicNode(uint val) => new(val);
   public static implicit operator DynamicNode(long val) => new(val);
   public static implicit operator DynamicNode(ulong val) => new((long)val);
   public static implicit operator DynamicNode(decimal val) => new(val);
   public static implicit operator DynamicNode(float val) => new(val);
   public static implicit operator DynamicNode(double val) => new(val);
   public static implicit operator DynamicNode(Guid val) => new(val);
   public static implicit operator DynamicNode(DateOnly val) => new(val);
   public static implicit operator DynamicNode(DateTime val) => new(val);
   public static implicit operator DynamicNode(DateTimeOffset val) => new(val);
   public static implicit operator DynamicNode(string val) => new(val);
   public static implicit operator DynamicNode(byte[] val) => new(val);
   public static implicit operator DynamicNode(Func<DynamicNode[], DynamicNode> val) => new(val);

   public static implicit operator bool?(DynamicNode obj) => obj.GetBoolean();
   public static implicit operator sbyte?(DynamicNode obj) => obj.GetInt8();
   public static implicit operator byte?(DynamicNode obj) => obj.GetUInt8();
   public static implicit operator short?(DynamicNode obj) => obj.GetInt16();
   public static implicit operator ushort?(DynamicNode obj) => obj.GetUInt16();
   public static implicit operator int?(DynamicNode obj) => obj.GetInt32();
   public static implicit operator uint?(DynamicNode obj) => obj.GetUInt32();
   public static implicit operator long?(DynamicNode obj) => obj.GetInt64();
   public static implicit operator ulong?(DynamicNode obj) => obj.GetUInt64();
   public static implicit operator decimal?(DynamicNode obj) => obj.GetDecimal();
   public static implicit operator double?(DynamicNode obj) => obj.GetFlt64();
   public static implicit operator float?(DynamicNode obj) => obj.GetFlt32();
   public static implicit operator Guid?(DynamicNode obj) => obj.GetGuid();
   public static implicit operator DateOnly?(DynamicNode obj) => obj.GetDateOnly();
   public static implicit operator DateTime?(DynamicNode obj) => obj.GetDateTimeLocal();
   public static implicit operator DateTimeOffset?(DynamicNode obj) => obj.GetDateTimeOffset();
   public static implicit operator string?(DynamicNode obj) => obj.GetString();
   public static implicit operator byte[]?(DynamicNode obj) => obj.GetBinary();
   public static implicit operator Func<DynamicNode[], DynamicNode>?(DynamicNode obj) => obj.AsFunction();

   #endregion

   #region Getters

   public bool IsNull()
   {
      return Type == DynamicNodeType.Null || Type == DynamicNodeType.Undefined;
   }

   public bool IsTrue()
   {
      return Type == DynamicNodeType.True ||
         (Type == DynamicNodeType.Integer && value.LongValue != 0) ||
         (Type == DynamicNodeType.Number && value.DoubleValue != 0) ||
         (Type == DynamicNodeType.String && refValue is string s && s.Length > 0 && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) ||
         (Type == DynamicNodeType.Decimal && (refValue is decimal d) && d > 0) ||
         (Type == DynamicNodeType.Guid && (refValue is Guid g) && g != Guid.Empty) ||
         (Type == DynamicNodeType.DateTimeLocal && value.LongValue != 0) ||
         (Type == DynamicNodeType.DateTimeUtc && value.LongValue != 0) ||
         ((Type == DynamicNodeType.DateTimeOffset || Type == DynamicNodeType.Array ||
           Type == DynamicNodeType.Object || Type == DynamicNodeType.Function) && refValue != null);
   }

   public bool? GetBoolean()
   {
      return Type switch
      {
         DynamicNodeType.Integer => value.LongValue != 0,
         DynamicNodeType.Number => value.DoubleValue != 0,
         DynamicNodeType.String => refValue is string s && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase),
         DynamicNodeType.Decimal => refValue is decimal d ? d != 0 : null,
         DynamicNodeType.True => true,
         DynamicNodeType.False => false,
         _ => null,
      };
   }

   public long? GetInt64()
   {
      return Type switch
      {
         DynamicNodeType.Integer => value.LongValue,
         DynamicNodeType.Number => (long)value.DoubleValue,
         DynamicNodeType.String => refValue is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
         DynamicNodeType.Decimal => refValue is decimal d ? (long)d : null,
         DynamicNodeType.DateTimeLocal => value.LongValue,
         DynamicNodeType.DateTimeUtc => value.LongValue,
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? o.ToUnixTimeMilliseconds() : null,
         DynamicNodeType.True => 1,
         DynamicNodeType.False => 0,
         _ => null,
      };
   }

   public ulong? GetUInt64()
   {
      return Type switch
      {
         DynamicNodeType.Integer => (ulong)value.LongValue,
         DynamicNodeType.Number => (ulong)value.DoubleValue,
         DynamicNodeType.String => refValue is string s && ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
         DynamicNodeType.Decimal => refValue is decimal d ? (ulong)d : null,
         DynamicNodeType.DateTimeLocal => (ulong)value.LongValue,
         DynamicNodeType.DateTimeUtc => (ulong)value.LongValue,
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? (ulong)o.ToUnixTimeMilliseconds() : null,
         DynamicNodeType.True => 1,
         DynamicNodeType.False => 0,
         _ => null,
      };
   }

   public decimal? GetDecimal()
   {
      return Type switch
      {
         DynamicNodeType.Integer => value.LongValue,
         DynamicNodeType.Number => (decimal)value.DoubleValue,
         DynamicNodeType.String => refValue is string s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var l) ? l : null,
         DynamicNodeType.Decimal => refValue is decimal d ? d : null,
         DynamicNodeType.DateTimeLocal => value.LongValue,
         DynamicNodeType.DateTimeUtc => value.LongValue,
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? o.ToUnixTimeMilliseconds() : null,
         DynamicNodeType.True => 1,
         DynamicNodeType.False => 0,
         _ => null,
      };
   }

   public sbyte? GetInt8() => (sbyte?)GetInt64();
   public byte? GetUInt8() => (byte?)GetUInt64();
   public short? GetInt16() => (short?)GetInt64();
   public ushort? GetUInt16() => (ushort?)GetUInt64();
   public int? GetInt32() => (int?)GetInt64();
   public uint? GetUInt32() => (uint?)GetUInt64();

   public double? GetFlt64()
   {
      return Type switch
      {
         DynamicNodeType.Integer => value.LongValue,
         DynamicNodeType.Number => value.DoubleValue,
         DynamicNodeType.String => refValue is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l : null,
         DynamicNodeType.Decimal => refValue is decimal d ? (double)d : null,
         DynamicNodeType.DateTimeLocal => value.LongValue,
         DynamicNodeType.DateTimeUtc => value.LongValue,
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? o.ToUnixTimeMilliseconds() : null,
         DynamicNodeType.True => 1,
         _ => null,
      };
   }

   public float? GetFlt32() => (float?)GetFlt64();

   public DateTime? GetDateTimeLocal()
   {
      return Type switch
      {
         DynamicNodeType.Integer => new DateTime(value.LongValue, DateTimeKind.Local),
         DynamicNodeType.Number => new DateTime((long)value.DoubleValue, DateTimeKind.Local),
         DynamicNodeType.String => refValue is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var l) ? l : null,
         DynamicNodeType.Decimal => refValue is decimal d ? new DateTime((long)d, DateTimeKind.Local) : null,
         DynamicNodeType.DateTimeLocal => new DateTime(value.LongValue, DateTimeKind.Local),
         DynamicNodeType.DateTimeUtc => new DateTime(value.LongValue, DateTimeKind.Utc).ToLocalTime(),
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? o.LocalDateTime : null,
         _ => null,
      };
   }

   public DateTime? GetDateTimeUtc()
   {
      return Type switch
      {
         DynamicNodeType.Integer => new DateTime(value.LongValue, DateTimeKind.Utc),
         DynamicNodeType.Number => new DateTime((long)value.DoubleValue, DateTimeKind.Utc),
         DynamicNodeType.String => refValue is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var l) ? l : null,
         DynamicNodeType.Decimal => refValue is decimal d ? new DateTime((long)d, DateTimeKind.Utc) : null,
         DynamicNodeType.DateTimeLocal => new DateTime(value.LongValue, DateTimeKind.Local).ToUniversalTime(),
         DynamicNodeType.DateTimeUtc => new DateTime(value.LongValue, DateTimeKind.Utc),
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? o.UtcDateTime : null,
         _ => null,
      };
   }

   public DateOnly? GetDateOnly()
   {
      var dt = GetDateTimeLocal();
      return dt.HasValue ? DateOnly.FromDateTime(dt.Value) : null;
   }

   public DateTimeOffset? GetDateTimeOffset()
   {
      return Type switch
      {
         DynamicNodeType.Integer => DateTimeOffset.FromUnixTimeMilliseconds(value.LongValue),
         DynamicNodeType.Number => DateTimeOffset.FromUnixTimeMilliseconds((long)value.DoubleValue),
         DynamicNodeType.String => refValue is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var l) ? l : null,
         DynamicNodeType.Decimal => refValue is decimal d ? DateTimeOffset.FromUnixTimeMilliseconds((long)d) : null,
         DynamicNodeType.DateTimeLocal => new DateTime(value.LongValue, DateTimeKind.Local),
         DynamicNodeType.DateTimeUtc => new DateTime(value.LongValue, DateTimeKind.Utc),
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? o : null,
         _ => null,
      };
   }

   public Guid? GetGuid()
   {
      return Type switch
      {
         DynamicNodeType.String => refValue is string s && Guid.TryParse(s, out var l) ? l : null,
         DynamicNodeType.Guid => refValue is Guid g ? g : null,
         DynamicNodeType.Binary => refValue is byte[] b && b.Length == 16 ? new Guid(b) : null,
         _ => null,
      };
   }

   public string? GetString()
   {
      return Type switch
      {
         DynamicNodeType.Integer => value.LongValue.ToString(CultureInfo.InvariantCulture),
         DynamicNodeType.Number => value.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
         DynamicNodeType.String => refValue as string,
         DynamicNodeType.Guid => refValue is Guid g ? g.ToString() : null,
         DynamicNodeType.Decimal => refValue is decimal d ? d.ToString(CultureInfo.InvariantCulture) : null,
         DynamicNodeType.DateTimeLocal => new DateTime(value.LongValue, DateTimeKind.Local).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
         DynamicNodeType.DateTimeUtc => new DateTime(value.LongValue, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
         DynamicNodeType.DateTimeOffset => refValue is DateTimeOffset o ? o.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null,
         DynamicNodeType.Binary => refValue is byte[] b ? Encoding.UTF8.GetString(b) : null,
         DynamicNodeType.True => "true",
         DynamicNodeType.False => "false",
         _ => null,
      };
   }

   public byte[]? GetBinary()
   {
      return Type switch
      {
         DynamicNodeType.String => refValue is string s ? Encoding.UTF8.GetBytes(s) : null,
         DynamicNodeType.Binary => refValue as byte[],
         DynamicNodeType.Guid => refValue is Guid g ? g.ToByteArray() : null,
         _ => null,
      };
   }

   public KeyValuePair<string?, DynamicNode> GetKeyValue()
   {
      if (Type == DynamicNodeType.Object && refValue is DynamicNodeObject o)
      {
         return (KeyValuePair<string?, DynamicNode>)o.FirstOrDefault()!;
      }
      return default;
   }

   public override string ToString()
   {
      return Type switch
      {
         DynamicNodeType.Integer => value.LongValue.ToString(CultureInfo.InvariantCulture),
         DynamicNodeType.Number => value.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
         DynamicNodeType.String => $"\"{refValue}\"",
         DynamicNodeType.Guid => refValue is Guid g ? $"\"{g}\"" : "",
         DynamicNodeType.Decimal => refValue is decimal d ? d.ToString(CultureInfo.InvariantCulture) : "0",
         DynamicNodeType.DateTimeLocal => $"\"{new DateTime(value.LongValue, DateTimeKind.Local).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\"",
         DynamicNodeType.DateTimeUtc => $"\"{new DateTime(value.LongValue, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\"",
         DynamicNodeType.DateTimeOffset => $"\"{(refValue is DateTimeOffset o ? o.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null)}\"",
         DynamicNodeType.True => "true",
         DynamicNodeType.False => "false",
         DynamicNodeType.Null => "null",
         DynamicNodeType.Undefined => "undefined",
         DynamicNodeType.Binary => "binary",
         DynamicNodeType.Object => refValue is DynamicNodeObject o ? $"{{{string.Join(",", o.Select(x => "\"" + x.Key + "\":" + (x.Value.GetString() ?? x.Value.Type.ToString().ToLower())))}}}" : "null",
         DynamicNodeType.Array => refValue is DynamicNodeArray a ? $"[{string.Join(",", a.Select(x => x.GetString() ?? x.Type.ToString().ToLower()))}]" : "null",
         _ => Type.ToString(),
      };
   }

   public DynamicNode DeepCopy()
   {
      if (refValue is DynamicNodeObject source)
      {
         var copy = NewObject();
         foreach (var pair in source) { copy[pair.Key] = pair.Value.DeepCopy(); }
         return copy;
      }
      if (refValue is DynamicNodeArray items)
      {
         var copy = NewArray();
         for (int i = 0; i < items.Count; i++) { copy.Push(items[i].DeepCopy()); }
         return copy;
      }
      return this;
   }

   public DynamicNodeObject? AsObject()
   {
      if (Type != DynamicNodeType.Object) { return null; }
      return refValue as DynamicNodeObject;
   }

   public DynamicNodeArray? AsArray()
   {
      if (Type != DynamicNodeType.Array) { return null; }
      return refValue as DynamicNodeArray;
   }

   public Func<DynamicNode[], DynamicNode>? AsFunction()
   {
      if (Type != DynamicNodeType.Function) { return null; }
      return refValue as Func<DynamicNode[], DynamicNode>;
   }

   public object? AsScalar()
   {
      return Type switch
      {
         DynamicNodeType.Integer => value.LongValue,
         DynamicNodeType.Number => value.DoubleValue,
         DynamicNodeType.String => refValue as string,
         DynamicNodeType.Decimal => refValue as decimal?,
         DynamicNodeType.Binary => refValue as byte[],
         DynamicNodeType.Guid => refValue as Guid?,
         DynamicNodeType.DateTimeLocal => new DateTime(value.LongValue, DateTimeKind.Local),
         DynamicNodeType.DateTimeUtc => new DateTime(value.LongValue, DateTimeKind.Utc),
         DynamicNodeType.DateTimeOffset => refValue as DateTimeOffset?,
         DynamicNodeType.True => true,
         DynamicNodeType.False => false,
         _ => null,
      };
   }

   public IEnumerable<string> GetPropertyNames()
   {
      return refValue is DynamicNodeObject o ? o.Keys : Array.Empty<string>();
   }

   public IEnumerable<KeyValuePair<string, DynamicNode>> GetProperties()
   {
      return refValue is DynamicNodeObject o ? o : Array.Empty<KeyValuePair<string, DynamicNode>>();
   }

   public IEnumerable<DynamicNode> GetArrayValues()
   {
      return refValue is IEnumerable<DynamicNode> a ? a : Array.Empty<DynamicNode>();
   }

   public bool HasProperty(string name)
   {
      if (name == null) { return false; }
      return (Type == DynamicNodeType.Object && refValue is DynamicNodeObject obj && obj.ContainsKey(name));
   }

   public DynamicNode GetProperty(string name)
   {
      if (name != null)
      {
         if (Type == DynamicNodeType.Object &&
             refValue is DynamicNodeObject obj &&
             obj.TryGetValue(name, out var value)) { return value; }
         if (Type == DynamicNodeType.Array &&
             refValue is DynamicNodeArray array &&
             int.TryParse(name, out var index) &&
             index >= 0 &&
             index < array.Count) { return array[index]; }
      }
      return Undefined;
   }

   public DynamicNode GetIndex(int index)
   {
      if (Type == DynamicNodeType.Array &&
          refValue is DynamicNodeArray array &&
          index >= 0 &&
          index < array.Count) { return array[index]; }
      if (Type == DynamicNodeType.Object &&
          refValue is DynamicNodeObject obj &&
          obj.TryGetValue(index.ToString(), out var value)) { return value; }
      return Undefined;
   }

   public DynamicNode Pop()
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         var index = array.Count - 1;
         if (index >= 0)
         {
            var val = array[index];
            array.RemoveAt(index);
            return val;
         }
      }
      return Undefined;
   }

   public DynamicNode Peek()
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         var index = array.Count - 1;
         if (index >= 0) { return array[index]; }
      }
      return Undefined;
   }

   public DynamicNode PeekOrSelf()
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         var index = array.Count - 1;
         if (index >= 0) { return array[index]; }
      }
      return this;
   }

   public DynamicNode Shift()
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         if (array.Count > 0)
         {
            var val = array[0];
            array.RemoveAt(0);
            return val;
         }
      }
      return Undefined;
   }

   public DynamicNode First()
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         if (array.Count > 0) { return array[0]; }
      }
      return Undefined;
   }

   public DynamicNode FirstOrSelf()
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         if (array.Count > 0) { return array[0]; }
      }
      return this;
   }

   public bool Contains(DynamicNode value)
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         return array.Contains(value);
      }
      return false;
   }

   public DynamicNode Invoke(params DynamicNode[] values)
   {
      if (Type == DynamicNodeType.Function && refValue is Func<DynamicNode[], DynamicNode> func)
      {
         return func(values);
      }
      return false;
   }

   public DynamicNode MaterializeEnumerable()
   {
      if (Type == DynamicNodeType.Enumerable && refValue is IEnumerable<DynamicNode> array) { return new(array); }
      return this;
   }

   #endregion

   #region Setters

   public void SetProperty(string name, DynamicNode value)
   {
      if (name != null)
      {
         if (Type == DynamicNodeType.Object && refValue is DynamicNodeObject obj)
         {
            obj[name] = value;
            return;
         }
         if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array && int.TryParse(name, out var index))
         {
#pragma warning disable CA2201
            if (index < 0) { throw new IndexOutOfRangeException(); }
#pragma warning restore CA2201
            while (array.Count < index) { array.Add(Undefined); }
            if (index == array.Count) { array.Add(value); }
            else { array[index] = value; }
            return;
         }
      }
      throw new InvalidOperationException();
   }

   public bool DeleteProperty(string name)
   {
      if (name != null)
      {
         if (Type == DynamicNodeType.Object && refValue is DynamicNodeObject obj)
         {
            return obj.Remove(name);
         }
         if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array && int.TryParse(name, out var index))
         {
            if (index < 0 || index >= array.Count) { return false; }
            array.RemoveAt(index);
            return true;
         }
      }
      return false;
   }

   public void Select(params string[] fields)
   {
      if (Type == DynamicNodeType.Object && refValue is DynamicNodeObject obj)
      {
         foreach (var field in obj.Keys.ToArray())
         {
            if (!fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
               obj.Remove(field);
            }
         }
      }
   }

   public void SetIndex(int index, DynamicNode value)
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
#pragma warning disable CA2201
         if (index < 0) { throw new IndexOutOfRangeException(); }
#pragma warning restore CA2201
         while (array.Count < index) { array.Add(Undefined); }
         if (index == array.Count) { array.Add(value); }
         else { array[index] = value; }
      }
      else if (Type == DynamicNodeType.Object && refValue is DynamicNodeObject obj)
      {
         obj[index.ToString()] = value;
      }
      else throw new InvalidOperationException();
   }

   public bool DeleteIndex(int index)
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         if (index < 0 || index >= array.Count) { return false; }
         array.RemoveAt(index);
         return true;
      }
      if (Type == DynamicNodeType.Object && refValue is DynamicNodeObject obj)
      {
         return obj.Remove(index.ToString());
      }
      return false;
   }

   public void Push(DynamicNode value)
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         array.Add(value);
         return;
      }
      throw new InvalidOperationException();
   }

   public void Unshift(DynamicNode value)
   {
      if (Type == DynamicNodeType.Array && refValue is DynamicNodeArray array)
      {
         array.Insert(0, value);
         return;
      }
      throw new InvalidOperationException();
   }

   #endregion

   #region Equals

   public bool IsEquivalent(DynamicNode other, HashSet<object>? hasChecked = null)
   {
      if (Equals(other)) { return true; }
      var objA = AsObject();
      var objB = other.AsObject();
      if (objA != null && objB != null)
      {
         if (objA.Count != objB.Count) { return false; }
         if (!(hasChecked ??= []).Add(objA)) { return true; }
         foreach (var pair in objA)
         {
            if (!objB.TryGetValue(pair.Key, out var valB)) { return false; }
            if (!pair.Value.IsEquivalent(valB, hasChecked)) { return false; }
         }
         return true;
      }
      var arrA = AsArray();
      var arrB = other.AsArray();
      if (arrA != null && arrB != null)
      {
         if (arrA.Count != arrB.Count) { return false; }
         if (!(hasChecked ??= []).Add(arrA)) { return true; }
         for (int i = 0, j = arrA.Count; i < j; i++)
         {
            if (!arrA[i].IsEquivalent(arrB[i], hasChecked)) { return false; }
         }
         return true;
      }
      return false;
   }

   public bool Equals(DynamicNode other)
   {
      if (Type != other.Type) { return false; }
      if (Type == DynamicNodeType.Null ||
          Type == DynamicNodeType.True ||
          Type == DynamicNodeType.False ||
          Type == DynamicNodeType.Undefined) { return true; }
      if (Type == DynamicNodeType.Integer ||
          Type == DynamicNodeType.DateTimeLocal ||
          Type == DynamicNodeType.DateTimeUtc) { return value.LongValue == other.value.LongValue; }
      if (Type == DynamicNodeType.Number) { return value.DoubleValue == other.value.DoubleValue; }
      if (Type == DynamicNodeType.Binary)
      {
         if (refValue is not byte[] a || other.refValue is not byte[] b) { return false; }
         if (a == b) { return true; }
         if (a.Length != b.Length) { return false; }
         for (int i = 0; i < a.Length; i++) { if (a[i] != b[i]) { return false; } }
         return true;
      }
      return refValue?.Equals(other.refValue) ?? false;
   }

   public override bool Equals([NotNullWhen(true)] object? obj)
   {
      return obj is DynamicNode other ? Equals(other) : false;
   }

   public override int GetHashCode()
   {
      return value.LongValue.GetHashCode() ^ (refValue?.GetHashCode() ?? 0);
   }

   public int CompareTo(DynamicNode other)
   {
      if (Type == DynamicNodeType.Null || Type == DynamicNodeType.Undefined)
      {
         return other.Type == DynamicNodeType.Null || other.Type == DynamicNodeType.Undefined ? 0 : -1;
      }
      if (other.Type == DynamicNodeType.Null || other.Type == DynamicNodeType.Undefined) { return 1; }
      if (Type == DynamicNodeType.Object || other.Type == DynamicNodeType.Object) { return 0; }
      if (Type == DynamicNodeType.Array || other.Type == DynamicNodeType.Array) { return 0; }
      if (Type == DynamicNodeType.Binary && other.Type == DynamicNodeType.Binary && refValue is byte[] a && other.refValue is byte[] b)
      {
         if (a.Length > b.Length) { return 1; } else if (a.Length < b.Length) { return -1; }
         for (int i = 0; i < a.Length; i++) { if (a[i] > b[i]) { return 1; } if (a[i] < b[i]) { return -1; } }
         return 0;
      }
      if (Type == DynamicNodeType.String || other.Type == DynamicNodeType.String) { return string.CompareOrdinal(GetString() ?? string.Empty, other.GetString() ?? string.Empty); }
      if (Type == DynamicNodeType.Number || other.Type == DynamicNodeType.Number) { return (GetFlt64() ?? 0).CompareTo(other.GetFlt64() ?? 0); }
      return (GetInt64() ?? 0).CompareTo(other.GetInt64() ?? 0);
   }

   #endregion

   #region Internal

   [StructLayout(LayoutKind.Explicit)]
   private readonly struct DynamicNodeValueType
   {
      [FieldOffset(0)] public readonly long LongValue;
      [FieldOffset(0)] public readonly double DoubleValue;

      public DynamicNodeValueType(long l) { LongValue = l; }
      public DynamicNodeValueType(double d) { DoubleValue = d; }
   }

   #endregion
}
