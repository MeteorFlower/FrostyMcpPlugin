using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.Managers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using FrostySdk.Managers.Entries;

// NOTE: Vendored copy of wavebend/FrostyToolsuite (DragonAge branch) EbxYamlWriter,
// adapted to run on stock CadeEvs FrostyToolsuite v1.0.6.3 (which has no
// EbxYamlWriter in FrostySdk). Signature mirrors FrostySdk's EbxXmlWriter:
// ctor(Stream, AssetManager) + WriteObjects(IEnumerable<object>).

namespace FrostySdk.IO
{
    public class EbxYamlWriter : IDisposable
    {
        private const BindingFlags PropertyBindingFlags = BindingFlags.Public | BindingFlags.Instance;

        // A large asset repeats the same few hundred types, so the sorted property
        // list and the __Guid field are resolved once per type. Concurrent because
        // exports run on worker threads.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> s_props =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> s_guidFields =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]>();

        private static PropertyInfo[] VisibleProps(Type type)
        {
            return s_props.GetOrAdd(type, t =>
            {
                PropertyInfo[] props = t.GetProperties(PropertyBindingFlags);
                Array.Sort(props, new PropertyComparer());

                List<PropertyInfo> visible = new List<PropertyInfo>(props.Length);
                foreach (PropertyInfo p in props)
                {
                    if (p.GetCustomAttribute<IsTransientAttribute>() == null)
                        visible.Add(p);
                }
                return visible.ToArray();
            });
        }

        private static FieldInfo[] GuidField(Type type)
        {
            return s_guidFields.GetOrAdd(type, t =>
            {
                FieldInfo fi = t.GetField("__Guid", BindingFlags.NonPublic | BindingFlags.Instance);
                return fi == null ? new FieldInfo[0] : new[] { fi };
            });
        }

        internal class PropertyComparer : IComparer<PropertyInfo>
        {
            public int Compare(PropertyInfo x, PropertyInfo y)
            {
                return (x.MetadataToken < y.MetadataToken) ? -1 : 1;
            }
        }

        private AssetManager am;
        private List<object> objs = new List<object>();
        private Stream stream;

        private int tabSize = 2;

        public EbxYamlWriter(Stream inStream, AssetManager inAm)
        {
            am = inAm;
            stream = inStream;
        }

        public void WriteObjects(IEnumerable<object> inObjs)
        {
            objs.Clear();
            objs.AddRange(inObjs);

            StringBuilder sb = new StringBuilder();
            foreach (object obj in objs)
                sb.Append(ClassToYaml(obj, obj.GetType(), tabSize));

            string value = sb.ToString();
            byte[] valueBuffer = Encoding.UTF8.GetBytes(value);

            stream.Write(valueBuffer, 0, valueBuffer.Length);
        }

        private string ClassToYaml(object Obj, Type ObjType, int TabCount = 0)
        {
            StringBuilder SB = new StringBuilder();
            PropertyInfo[] Properties = VisibleProps(ObjType);

            string StrGuid = "";
            FieldInfo[] guidField = GuidField(ObjType);
            if (guidField.Length > 0)
            {
                AssetClassGuid Guid = (AssetClassGuid)guidField[0].GetValue(Obj);
                StrGuid = " <" + Guid.ToString() + ">";
            }

            SB.AppendLine();
            SB.Append("".PadLeft(TabCount) + ObjType.Name + StrGuid + " : ");

            if (Properties.Length > 0)
            {
                TabCount += tabSize;
                foreach (PropertyInfo PI in Properties)
                {
                    object Value = PI.GetValue(Obj);
                    string Tmp = "";

                    string fieldValue = FieldToYaml(Value, ref Tmp, TabCount);

                    SB.AppendLine();
                    SB.Append("".PadLeft(TabCount) + PI.Name + Tmp + " : ");
                    SB.Append(fieldValue);
                }
                TabCount -= tabSize;
            }

            return SB.ToString();
        }

        private string FieldToYaml(object Value, ref string AdditionalInfo, int TabCount = 0)
        {
            if (Value == null)
            {
                AdditionalInfo = "";
                return "null";
            }

            Type FieldType = Value.GetType();
            StringBuilder SB = new StringBuilder();

            // IList instead of get_Count / get_Item through reflection: the list
            // branch used to build a MethodInfo per list and invoke it per element.
            if (FieldType.Name == "List`1" && Value is IList items)
            {
                AdditionalInfo = " [Count=" + items.Count + "]";

                if (items.Count > 0)
                {
                    TabCount += tabSize;
                    for (int i = 0; i < items.Count; i++)
                    {
                        SB.AppendLine();
                        SB.Append("".PadLeft(TabCount) + "- member [" + i.ToString() + "] : ");

                        string Tmp = "";
                        SB.Append(FieldToYaml(items[i], ref Tmp, TabCount));
                    }
                    TabCount -= tabSize;
                }
            }
            else
            {
                if (FieldType.Namespace == "FrostySdk.Ebx" && FieldType.BaseType != typeof(Enum))
                {
                    if (FieldType == typeof(CString)) SB.Append(Value.ToString());
                    else if (FieldType == typeof(ResourceRef)) SB.Append(Value.ToString());
                    else if (FieldType == typeof(FileRef)) SB.Append(Value.ToString());
                    else if (FieldType == typeof(TypeRef)) SB.Append(Value.ToString());
                    else if (FieldType == typeof(BoxedValueRef)) SB.Append(Value.ToString());
                    else if (FieldType == typeof(PointerRef))
                    {
                        PointerRef Reference = (PointerRef)Value;
                        if (Reference.Type == PointerRefType.Internal)
                        {
                            Type SubObjType = Reference.Internal.GetType();
                            AssetClassGuid guid = (AssetClassGuid)SubObjType.GetField("__Guid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Reference.Internal);
                            SB.Append("[" + SubObjType.Name + "] " + guid.ToString());
                        }
                        else if (Reference.Type == PointerRefType.External)
                        {
                            EbxAssetEntry entry = am.GetEbxEntry(Reference.External.FileGuid);
                            if (entry != null)
                            {
                                SB.Append("[Ebx] " + entry.Name + " [" + Reference.External.ClassGuid + "]");
                            }
                            else
                            {
                                SB.Append("[Ebx] BadRef " + Reference.External.FileGuid + "/" + Reference.External.ClassGuid);
                            }
                        }
                        else
                            SB.Append("nullptr");
                    }
                    else
                    {
                        TabCount += tabSize;
                        SB.Append(ClassToYaml(Value, Value.GetType(), TabCount));
                        TabCount -= tabSize;
                    }
                }
                else
                {
                    if (FieldType == typeof(byte)) SB.Append(((byte)Value).ToString("X2"));
                    else if (FieldType == typeof(ushort)) SB.Append(((ushort)Value).ToString("X4"));
                    else if (FieldType == typeof(uint))
                    {
                        uint value = (uint)Value;
                        string val = Utils.GetString((int)value);

                        if (!val.StartsWith("0x"))
                        {
                            SB.Append(val + " [" + value.ToString("X8") + "]");
                        }
                        else
                        {
                            SB.Append(val);
                        }
                    }
                    else if (FieldType == typeof(int))
                    {
                        int value = (int)Value;
                        string val = Utils.GetString(value);

                        if (!val.StartsWith("0x"))
                        {
                            SB.Append(val + " [" + value.ToString("X8") + "]");
                        }
                        else
                        {
                            SB.Append(val);
                        }
                    }
                    else if (FieldType == typeof(ulong)) SB.Append(((ulong)Value).ToString("X16"));
                    else if (FieldType == typeof(float)) SB.Append(((float)Value).ToString());
                    else if (FieldType == typeof(double)) SB.Append(((double)Value).ToString());
                    else SB.Append(Value.ToString());
                }
            }

            return SB.ToString();
        }

        public void Dispose() => Dispose(true);

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stream copyOfStream = stream;
                stream = null;
                if (copyOfStream != null)
                {
                    copyOfStream.Close();
                }
            }
            stream = null;
        }
    }
}
