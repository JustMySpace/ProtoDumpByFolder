using Google.Protobuf;
using Google.Protobuf.Reflection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ProtoFolderDump48
{
    internal static class Program
    {
        private static List<string> _probeDirs = new List<string>();
        private static readonly bool _verbose = true;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("用法: ProtoFolderDump48 <folderPath> [TypeNamesCsv]");
                return 1;
            }

            var rootDir = args[0];
            if (!Directory.Exists(rootDir))
            {
                Console.WriteLine("目录不存在: " + rootDir);
                return 2;
            }

            var targetedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (args.Length >= 2)
            {
                foreach (var n in args[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    targetedNames.Add(n.Trim());
            }

            Directory.CreateDirectory("out");

            var asmPaths = Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories)
                .Where(p =>
                {
                    var ext = Path.GetExtension(p);
                    return ext != null && (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                                           ext.Equals(".exe", StringComparison.OrdinalIgnoreCase));
                })
                .ToList();

            _probeDirs = asmPaths.Select(p => Path.GetDirectoryName(p))
                                 .Where(d => !string.IsNullOrEmpty(d))
                                 .Select(d => Path.GetFullPath(d))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            Console.WriteLine("扫描程序集数量: " + asmPaths.Count);

            var set = new FileDescriptorSet();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int loadedAssemblies = 0;
            int hitReflectionRoots = 0;
            int hitMessageRoots = 0;
            int hitEnumRoots = 0;
            int hitTargeted = 0;

            foreach (var asmPath in asmPaths)
            {
                var asm = SafeLoadAssembly(asmPath);
                if (asm == null) continue;
                loadedAssemblies++;
                Console.WriteLine("[load] " + asmPath);

                // 导出嵌入式 .proto（如果有）
                DumpEmbeddedProtos(asm);

                // 定向抓取（优先）
                if (targetedNames.Count > 0)
                {
                    hitTargeted += TryCollectByTargetedNames(asm, targetedNames, set, seenNames);
                }

                // 兜底 1：*Reflection 静态根
                hitReflectionRoots += TryCollectByReflectionTypes(asm, set, seenNames);

                // 兜底 2：消息类型静态 Descriptor
                hitMessageRoots += TryCollectByMessageTypes(asm, set, seenNames);

                // 兜底 3：枚举类型静态 Descriptor
                hitEnumRoots += TryCollectByEnumTypes(asm, set, seenNames);
            }

            if (set.File.Count == 0)
            {
                Console.WriteLine("未找到任何 Protobuf Reflection 定义。");
                Console.WriteLine(@"建议：追加你已知的类型名，例如：
  ProtoFolderDump48.exe ""C:\...\发布目录"" ImageDisplayFrameInfo,ImageRgb3Reflection");
                return 0;
            }

            // 写 descriptor set
            var descPath = Path.Combine("out", "descriptors.desc");
            using (var fs = File.Create(descPath))
                set.WriteTo(fs);
            Console.WriteLine("DescriptorSet 导出完成: " + descPath + " (files: " + set.File.Count + ")");

            // 输出 .proto（保持 FileDescriptorProto.Name 的相对路径；不加 .recovered）
            foreach (var f in set.File)
            {
                var rel = SanitizeRelativePathOrFallback(f.Name);
                var outPath = Path.Combine("out", rel);
                var outDir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

                File.WriteAllText(outPath, PrintProto(f), Encoding.UTF8);
                Console.WriteLine("  + " + outPath);
            }

            Console.WriteLine("完成。已加载程序集: " + loadedAssemblies
                + ", 定向命中: " + hitTargeted
                + ", *Reflection 命中: " + hitReflectionRoots
                + ", IMessage 命中: " + hitMessageRoots
                + ", Enum 命中: " + hitEnumRoots);

            return 0;
        }

        // ---------------- Assembly 加载/解析 ----------------

        private static Assembly SafeLoadAssembly(string path)
        {
            try { return Assembly.LoadFrom(Path.GetFullPath(path)); }
            catch { return null; }
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name + ".dll";
                foreach (var dir in _probeDirs)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        try { return Assembly.LoadFrom(candidate); } catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private static IEnumerable<Type> GetTypesSafe(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Array.Empty<Type>(); }
        }

        // ---------------- 模式 0：定向类型名（最可靠） ----------------

        private static int TryCollectByTargetedNames(Assembly asm, HashSet<string> names, FileDescriptorSet set, HashSet<string> seen)
        {
            int hits = 0;
            foreach (var t in GetTypesSafe(asm))
            {
                if (t == null) continue;

                if (!names.Contains(t.FullName ?? string.Empty) && !names.Contains(t.Name))
                    continue;

                if (_verbose) Console.WriteLine("  [target] " + (t.FullName ?? t.Name));

                var descObj = TryGetStaticDescriptorObject(t);
                if (descObj != null)
                {
                    if (IsTypeName(descObj, "Google.Protobuf.Reflection.FileDescriptor"))
                    {
                        if (TryAddFileDescriptorRecursive(descObj, set, seen)) hits++;
                        continue;
                    }

                    var fileObj = GetProperty(descObj, "File");
                    if (fileObj != null && IsTypeName(fileObj, "Google.Protobuf.Reflection.FileDescriptor"))
                    {
                        if (TryAddFileDescriptorRecursive(fileObj, set, seen)) hits++;
                        continue;
                    }
                }

                foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var nDesc = TryGetStaticDescriptorObject(nt);
                    if (nDesc == null) continue;

                    if (IsTypeName(nDesc, "Google.Protobuf.Reflection.FileDescriptor"))
                    {
                        if (TryAddFileDescriptorRecursive(nDesc, set, seen)) hits++;
                    }
                    else
                    {
                        var fileObj = GetProperty(nDesc, "File");
                        if (fileObj != null && IsTypeName(fileObj, "Google.Protobuf.Reflection.FileDescriptor"))
                        {
                            if (TryAddFileDescriptorRecursive(fileObj, set, seen)) hits++;
                        }
                    }
                }
            }
            return hits;
        }

        // ---------------- 入口 1：*Reflection 静态根 ----------------
        private static int TryCollectByReflectionTypes(Assembly asm, FileDescriptorSet set, HashSet<string> seen)
        {
            int hits = 0;
            foreach (var t in GetTypesSafe(asm))
            {
                if (t == null || !t.IsClass) continue;
                if (!t.Name.EndsWith("Reflection", StringComparison.Ordinal)) continue;

                object fd = TryGetStaticDescriptorObject(t);
                if (fd == null) continue;

                if (TryAddFileDescriptorRecursive(fd, set, seen)) hits++;
            }
            return hits;
        }

        // ---------------- 入口 2：消息类型静态 Descriptor ----------------
        private static int TryCollectByMessageTypes(Assembly asm, FileDescriptorSet set, HashSet<string> seen)
        {
            int hits = 0;
            foreach (var t in GetTypesSafe(asm))
            {
                if (t == null || !t.IsClass) continue;

                var mdObj = TryGetStaticDescriptorObject(t); // MessageDescriptor or null
                if (mdObj == null) continue;

                var fileObj = GetProperty(mdObj, "File");
                if (fileObj == null) continue;

                if (TryAddFileDescriptorRecursive(fileObj, set, seen)) hits++;
            }
            return hits;
        }

        // ---------------- 入口 3：枚举类型静态 Descriptor ----------------
        private static int TryCollectByEnumTypes(Assembly asm, FileDescriptorSet set, HashSet<string> seen)
        {
            int hits = 0;
            foreach (var t in GetTypesSafe(asm))
            {
                if (t == null || !t.IsEnum) continue;

                var edObj = TryGetStaticDescriptorObject(t); // EnumDescriptor or null
                if (edObj == null) continue;

                var fileObj = GetProperty(edObj, "File");
                if (fileObj == null) continue;

                if (TryAddFileDescriptorRecursive(fileObj, set, seen)) hits++;
            }
            return hits;
        }

        // 取静态 Descriptor（兼容属性/方法）
        private static object TryGetStaticDescriptorObject(Type t)
        {
            try
            {
                var prop = t.GetProperty("Descriptor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) return prop.GetValue(null, null);

                var get = t.GetMethod("get_Descriptor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (get != null) return get.Invoke(null, null);
            }
            catch { }
            return null;
        }

        // ---------------- FileDescriptor 递归收集（先尝试 bytes，失败则重建） ----------------

        private static bool TryAddFileDescriptorRecursive(object fileDescriptorObj, FileDescriptorSet set, HashSet<string> seen)
        {
            try
            {
                var name = GetStringProperty(fileDescriptorObj, "Name");
                if (string.IsNullOrEmpty(name)) name = Guid.NewGuid().ToString("N") + ".proto";

                if (!seen.Add(name))
                    return false;

                // 递归依赖
                foreach (var dep in GetEnumerableProperty(fileDescriptorObj, "Dependencies"))
                {
                    if (dep != null)
                        TryAddFileDescriptorRecursive(dep, set, seen);
                }

                // A. 先尝试直接拿 bytes
                var fdProtoBytes = GetFileDescriptorProtoBytes(fileDescriptorObj);
                FileDescriptorProto localProto = null;

                if (fdProtoBytes != null && fdProtoBytes.Length > 0)
                {
                    localProto = FileDescriptorProto.Parser.ParseFrom(fdProtoBytes);
                }
                else
                {
                    // B. 无法拿 bytes，则反射重建
                    localProto = BuildFileDescriptorProto(fileDescriptorObj);
                }

                if (localProto != null)
                {
                    // 若 Name 为空，回填一个稳定名字（避免导出文件名空白）
                    if (string.IsNullOrEmpty(localProto.Name))
                        localProto.Name = name;

                    set.File.Add(localProto);
                    Console.WriteLine("  [fd] " + localProto.Name);
                }
                else
                {
                    Console.WriteLine("  [fd-warn] 既无法获取 Proto 字节，也无法重建: " + name);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [fd-skip] 处理失败: " + ex.Message);
                return false;
            }
        }

        // 通过 Proto 属性或 ToProto() 方法拿到 FileDescriptorProto 字节
        private static byte[] GetFileDescriptorProtoBytes(object fileDescriptorObj)
        {
            object protoObj = null;

            protoObj = GetProperty(fileDescriptorObj, "Proto");
            if (protoObj == null)
            {
                protoObj = InvokeMethod(fileDescriptorObj, "ToProto");
            }
            if (protoObj == null) return null;

            var bytesObj = InvokeMethod(protoObj, "ToByteArray");
            return bytesObj as byte[];
        }

        // ---------------- 用反射重建 FileDescriptorProto ----------------

        private static FileDescriptorProto BuildFileDescriptorProto(object fileObj)
        {
            var fdp = new FileDescriptorProto();

            fdp.Name = GetStringProperty(fileObj, "Name") ?? "";
            fdp.Package = GetStringProperty(fileObj, "Package") ?? "";

            var syntaxObj = GetProperty(fileObj, "Syntax");
            if (syntaxObj != null)
            {
                var s = syntaxObj.ToString();
                if (s.IndexOf('3') >= 0 || s.IndexOf("Proto3", StringComparison.OrdinalIgnoreCase) >= 0) fdp.Syntax = "proto3";
                else if (s.IndexOf('2') >= 0 || s.IndexOf("Proto2", StringComparison.OrdinalIgnoreCase) >= 0) fdp.Syntax = "proto2";
            }
            if (string.IsNullOrEmpty(fdp.Syntax)) fdp.Syntax = "proto3";

            foreach (var dep in GetEnumerableProperty(fileObj, "Dependencies"))
            {
                var depName = GetStringProperty(dep, "Name");
                if (!string.IsNullOrEmpty(depName))
                    fdp.Dependency.Add(depName);
            }

            foreach (var en in GetEnumerableProperty(fileObj, "EnumTypes"))
            {
                var enp = BuildEnumDescriptorProto(en);
                if (enp != null) fdp.EnumType.Add(enp);
            }

            foreach (var m in GetEnumerableProperty(fileObj, "MessageTypes"))
            {
                var mdp = BuildMessageDescriptorProto(m);
                if (mdp != null) fdp.MessageType.Add(mdp);
            }

            foreach (var svc in GetEnumerableProperty(fileObj, "Services"))
            {
                var sp = BuildServiceDescriptorProto(svc);
                if (sp != null) fdp.Service.Add(sp);
            }

            return fdp;
        }

        private static EnumDescriptorProto BuildEnumDescriptorProto(object enumObj)
        {
            if (enumObj == null) return null;
            var enp = new EnumDescriptorProto
            {
                Name = GetStringProperty(enumObj, "Name") ?? "Enum"
            };

            foreach (var v in GetEnumerableProperty(enumObj, "Values"))
            {
                var ev = new EnumValueDescriptorProto
                {
                    Name = GetStringProperty(v, "Name") ?? "Value",
                    Number = SafeGetInt32(v, "Number") // EnumValueDescriptor 的 Number
                };
                enp.Value.Add(ev);
            }
            return enp;
        }

        private static DescriptorProto BuildMessageDescriptorProto(object msgObj)
        {
            if (msgObj == null) return null;
            var mdp = new DescriptorProto
            {
                Name = GetStringProperty(msgObj, "Name") ?? "Message"
            };

            var oneofs = ConvertToList(GetEnumerableProperty(msgObj, "Oneofs"));
            for (int i = 0; i < oneofs.Count; i++)
            {
                var name = GetStringProperty(oneofs[i], "Name") ?? $"oneof_{i}";
                mdp.OneofDecl.Add(new OneofDescriptorProto { Name = name });
            }

            IEnumerable<object> fieldEnum = null;
            var fieldsColl = GetProperty(msgObj, "Fields");
            if (fieldsColl != null)
            {
                fieldEnum = InvokeMethod(fieldsColl, "InDeclarationOrder") as IEnumerable<object>;
                if (fieldEnum == null)
                    fieldEnum = fieldsColl as IEnumerable<object>;
            }

            if (fieldEnum != null)
            {
                foreach (var f in fieldEnum)
                {
                    var fdp = BuildFieldDescriptorProto(f, oneofs);
                    if (fdp != null) mdp.Field.Add(fdp);
                }
            }

            foreach (var en in GetEnumerableProperty(msgObj, "EnumTypes"))
            {
                var enp = BuildEnumDescriptorProto(en);
                if (enp != null) mdp.EnumType.Add(enp);
            }

            foreach (var nm in GetEnumerableProperty(msgObj, "NestedTypes"))
            {
                var nmdp = BuildMessageDescriptorProto(nm);
                if (nmdp != null) mdp.NestedType.Add(nmdp);
            }

            return mdp;
        }

        private static FieldDescriptorProto BuildFieldDescriptorProto(object fieldObj, List<object> oneofs)
        {
            if (fieldObj == null) return null;

            var fdp = new FieldDescriptorProto
            {
                Name = GetStringProperty(fieldObj, "Name") ?? "field",
                Number = SafeGetInt32(fieldObj, "FieldNumber"), // ★ 修复：FieldDescriptor 的属性名是 FieldNumber
                Label = FieldDescriptorProto.Types.Label.Optional, // 默认 optional（proto3 默认为 optional）
                Type = FieldDescriptorProto.Types.Type.String      // 占位，稍后映射
            };

            // 若仍为 0，再尝试 "Number" 作兜底（极少数自定义实现）
            if (fdp.Number == 0)
                fdp.Number = SafeGetInt32(fieldObj, "Number");

            var isRepeatedObj = GetProperty(fieldObj, "IsRepeated");
            if (isRepeatedObj is bool b && b)
                fdp.Label = FieldDescriptorProto.Types.Label.Repeated;

            var containingOneof = GetProperty(fieldObj, "ContainingOneof");
            if (containingOneof != null && oneofs != null && oneofs.Count > 0)
            {
                var name = GetStringProperty(containingOneof, "Name");
                var idx = oneofs.FindIndex(x => string.Equals(GetStringProperty(x, "Name"), name, StringComparison.Ordinal));
                if (idx >= 0) fdp.OneofIndex = idx;
            }

            var fieldTypeObj = GetProperty(fieldObj, "FieldType") ?? GetProperty(fieldObj, "Type");
            var typeName = fieldTypeObj?.ToString() ?? "";

            switch (typeName)
            {
                case "Double": fdp.Type = FieldDescriptorProto.Types.Type.Double; break;
                case "Float": fdp.Type = FieldDescriptorProto.Types.Type.Float; break;
                case "Int64": fdp.Type = FieldDescriptorProto.Types.Type.Int64; break;
                case "UInt64": fdp.Type = FieldDescriptorProto.Types.Type.Uint64; break;
                case "Int32": fdp.Type = FieldDescriptorProto.Types.Type.Int32; break;
                case "Fixed64": fdp.Type = FieldDescriptorProto.Types.Type.Fixed64; break;
                case "Fixed32": fdp.Type = FieldDescriptorProto.Types.Type.Fixed32; break;
                case "Bool": fdp.Type = FieldDescriptorProto.Types.Type.Bool; break;
                case "String": fdp.Type = FieldDescriptorProto.Types.Type.String; break;
                case "Bytes": fdp.Type = FieldDescriptorProto.Types.Type.Bytes; break;
                case "UInt32": fdp.Type = FieldDescriptorProto.Types.Type.Uint32; break;
                case "SFixed32": fdp.Type = FieldDescriptorProto.Types.Type.Sfixed32; break;
                case "SFixed64": fdp.Type = FieldDescriptorProto.Types.Type.Sfixed64; break;
                case "SInt32": fdp.Type = FieldDescriptorProto.Types.Type.Sint32; break;
                case "SInt64": fdp.Type = FieldDescriptorProto.Types.Type.Sint64; break;

                case "Enum":
                    fdp.Type = FieldDescriptorProto.Types.Type.Enum;
                    var enumType = GetProperty(fieldObj, "EnumType");
                    var enumFull = GetStringProperty(enumType, "FullName");
                    if (!string.IsNullOrEmpty(enumFull)) fdp.TypeName = "." + enumFull;
                    break;

                case "Message":
                    fdp.Type = FieldDescriptorProto.Types.Type.Message;
                    var msgType = GetProperty(fieldObj, "MessageType");
                    var msgFull = GetStringProperty(msgType, "FullName");
                    if (!string.IsNullOrEmpty(msgFull)) fdp.TypeName = "." + msgFull;
                    break;

                default:
                    // 少见的 Group/未知：降级为 bytes，保证生成
                    fdp.Type = FieldDescriptorProto.Types.Type.Bytes;
                    break;
            }

            return fdp;
        }

        private static ServiceDescriptorProto BuildServiceDescriptorProto(object svcObj)
        {
            if (svcObj == null) return null;

            var sdp = new ServiceDescriptorProto
            {
                Name = GetStringProperty(svcObj, "Name") ?? "Service"
            };

            foreach (var m in GetEnumerableProperty(svcObj, "Methods"))
            {
                var mdp = new MethodDescriptorProto
                {
                    Name = GetStringProperty(m, "Name") ?? "Method",
                    ClientStreaming = SafeGetBool(m, "IsClientStreaming"),
                    ServerStreaming = SafeGetBool(m, "IsServerStreaming")
                };

                var inType = GetProperty(m, "InputType");
                var outType = GetProperty(m, "OutputType");
                var inFull = GetStringProperty(inType, "FullName");
                var outFull = GetStringProperty(outType, "FullName");
                if (!string.IsNullOrEmpty(inFull)) mdp.InputType = "." + inFull;
                if (!string.IsNullOrEmpty(outFull)) mdp.OutputType = "." + outFull;

                sdp.Method.Add(mdp);
            }

            return sdp;
        }

        // ---------------- 打印 .proto（保持原始 import 路径） ----------------

        private static string PrintProto(FileDescriptorProto f)
        {
            var sb = new StringBuilder();
            var syntax = string.IsNullOrEmpty(f.Syntax) ? "proto3" : f.Syntax;
            sb.AppendLine("syntax = \"" + syntax + "\";");
            if (!string.IsNullOrEmpty(f.Package))
                sb.AppendLine("package " + f.Package + ";");
            foreach (var dep in f.Dependency)
                sb.AppendLine("import \"" + dep + "\";");
            sb.AppendLine();

            foreach (var en in f.EnumType) sb.Append(PrintEnum(en));
            foreach (var m in f.MessageType) sb.Append(PrintMessage(m));
            foreach (var s in f.Service) sb.Append(PrintService(s));

            return sb.ToString();
        }

        private static string PrintEnum(EnumDescriptorProto en)
        {
            var sb = new StringBuilder();
            sb.AppendLine("enum " + en.Name + " {");
            foreach (var v in en.Value)
                sb.AppendLine("  " + v.Name + " = " + v.Number + ";");
            sb.AppendLine("}");
            sb.AppendLine();
            return sb.ToString();
        }

        private static string PrintMessage(DescriptorProto m, string indent = "")
        {
            var sb = new StringBuilder();
            sb.AppendLine(indent + "message " + m.Name + " {");

            foreach (var f in m.Field)
            {
                var label = (f.Label == FieldDescriptorProto.Types.Label.Repeated) ? "repeated " : "";
                var typeName = ToTypeName(f);
                sb.AppendLine(indent + "  " + label + typeName + " " + f.Name + " = " + f.Number + ";");
            }

            foreach (var en in m.EnumType)
                sb.Append(PrintEnumIndented(en, indent + "  "));
            foreach (var nested in m.NestedType)
                sb.Append(PrintMessage(nested, indent + "  "));

            sb.AppendLine(indent + "}");
            sb.AppendLine();
            return sb.ToString();
        }

        private static string ToTypeName(FieldDescriptorProto f)
        {
            switch (f.Type)
            {
                case FieldDescriptorProto.Types.Type.Double: return "double";
                case FieldDescriptorProto.Types.Type.Float: return "float";
                case FieldDescriptorProto.Types.Type.Int64: return "int64";
                case FieldDescriptorProto.Types.Type.Uint64: return "uint64";
                case FieldDescriptorProto.Types.Type.Int32: return "int32";
                case FieldDescriptorProto.Types.Type.Fixed64: return "fixed64";
                case FieldDescriptorProto.Types.Type.Fixed32: return "fixed32";
                case FieldDescriptorProto.Types.Type.Bool: return "bool";
                case FieldDescriptorProto.Types.Type.String: return "string";
                case FieldDescriptorProto.Types.Type.Bytes: return "bytes";
                case FieldDescriptorProto.Types.Type.Uint32: return "uint32";
                case FieldDescriptorProto.Types.Type.Sfixed32: return "sfixed32";
                case FieldDescriptorProto.Types.Type.Sfixed64: return "sfixed64";
                case FieldDescriptorProto.Types.Type.Sint32: return "sint32";
                case FieldDescriptorProto.Types.Type.Sint64: return "sint64";
                case FieldDescriptorProto.Types.Type.Enum: return TrimDot(f.TypeName);
                case FieldDescriptorProto.Types.Type.Message: return TrimDot(f.TypeName);
                default: return f.Type.ToString();
            }
        }

        private static string PrintEnumIndented(EnumDescriptorProto en, string indent)
        {
            var sb = new StringBuilder();
            sb.AppendLine(indent + "enum " + en.Name + " {");
            foreach (var v in en.Value)
                sb.AppendLine(indent + "  " + v.Name + " = " + v.Number + ";");
            sb.AppendLine(indent + "}");
            return sb.ToString();
        }

        private static string PrintService(ServiceDescriptorProto s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("service " + s.Name + " {");
            foreach (var m in s.Method)
            {
                var inType = TrimDot(m.InputType);
                var outType = TrimDot(m.OutputType);
                var cs = m.ClientStreaming ? "stream " : "";
                var ss = m.ServerStreaming ? "stream " : "";
                sb.AppendLine("  rpc " + m.Name + " (" + cs + inType + ") returns (" + ss + outType + ");");
            }
            sb.AppendLine("}");
            sb.AppendLine();
            return sb.ToString();
        }

        // ---------------- 杂项工具 ----------------

        private static object GetProperty(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return null;
            try { return p.GetValue(obj, null); } catch { return null; }
        }

        private static string GetStringProperty(object obj, string name)
        {
            var v = GetProperty(obj, name);
            return v as string;
        }

        private static IEnumerable<object> GetEnumerableProperty(object obj, string name)
        {
            var v = GetProperty(obj, name);
            if (v == null) yield break;

            var en = v as IEnumerable;
            if (en == null) yield break;

            foreach (var item in en)
                yield return item;
        }

        private static object InvokeMethod(object obj, string name, params object[] args)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m == null) return null;
            try { return m.Invoke(obj, args); } catch { return null; }
        }

        private static int SafeGetInt32(object obj, string prop)
        {
            var v = GetProperty(obj, prop);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        private static bool SafeGetBool(object obj, string prop)
        {
            var v = GetProperty(obj, prop);
            if (v == null) return false;
            try { return Convert.ToBoolean(v); } catch { return false; }
        }

        private static bool IsTypeName(object obj, string fullName)
        {
            return obj != null && string.Equals(obj.GetType().FullName, fullName, StringComparison.Ordinal);
        }

        private static List<object> ConvertToList(IEnumerable<object> src)
        {
            var list = new List<object>();
            if (src == null) return list;
            foreach (var x in src) list.Add(x);
            return list;
        }

        private static string TrimDot(string s)
        {
            return (!string.IsNullOrEmpty(s) && s.StartsWith(".")) ? s.Substring(1) : s;
        }

        // 将 FileDescriptorProto.Name 转为相对路径（保留目录层级），每段做文件名安全化
        private static string SanitizeRelativePathOrFallback(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "unnamed/" + Guid.NewGuid().ToString("N") + ".proto";

            // 统一使用 '/' 作为分隔，再按段处理
            var parts = name.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var safeParts = parts.Select(SanitizeFileNamePart).ToArray();

            var safeRel = Path.Combine(safeParts);
            if (!safeRel.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
                safeRel += ".proto";
            return safeRel;
        }

        private static string SanitizeFileNamePart(string part)
        {
            if (string.IsNullOrEmpty(part)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = part.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars);
        }

        private static void DumpEmbeddedProtos(Assembly asm)
        {
            try
            {
                var names = asm.GetManifestResourceNames()
                               .Where(n => n.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
                               .ToArray();
                if (names.Length == 0) return;

                var asmId = SanitizeFileNamePart(Path.GetFileNameWithoutExtension(asm.Location) ?? asm.GetName().Name);
                var outDir = Path.Combine("out", "resources", asmId);
                Directory.CreateDirectory(outDir);

                foreach (var res in names)
                {
                    using (var s = asm.GetManifestResourceStream(res))
                    {
                        if (s == null) continue;
                        var fileName = res.Replace('/', '_').Replace('\\', '_');
                        fileName = SanitizeFileNamePart(fileName);
                        if (!fileName.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
                            fileName += ".proto";
                        var full = Path.Combine(outDir, fileName);
                        using (var fs = File.Create(full))
                            s.CopyTo(fs);
                        Console.WriteLine("  [res] " + full);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [res-skip] " + (asm.FullName ?? "<unknown>") + " : " + ex.Message);
            }
        }
    }
}
