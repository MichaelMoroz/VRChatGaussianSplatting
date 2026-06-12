// SPDX-License-Identifier: MIT
#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;

namespace GaussianSplatting.Editor.Utils
{
    public static class PLYFileReader
    {
        public static void ReadFileHeader(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs)
        {
            vertexCount = 0;
            vertexStride = 0;
            attrs = new List<(string, ElementType)>();
            if (!File.Exists(filePath))
                return;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, fs);
        }

        static void ReadHeaderImpl(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs, FileStream fs)
        {
            // read header
            vertexCount = 0;
            vertexStride = 0;
            attrs = new List<(string, ElementType)>();
            const int kMaxHeaderLines = 9000;
            bool got_binary_le = false;
            bool inVertexElement = false;
            bool gotEndHeader = false;
            for (int lineIdx = 0; lineIdx < kMaxHeaderLines; ++lineIdx)
            {
                var line = ReadLine(fs);
                if (line == "end_header")
                {
                    gotEndHeader = true;
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                if (tokens.Length >= 3 && tokens[0] == "format")
                {
                    got_binary_le = tokens[1] == "binary_little_endian" && tokens[2] == "1.0";
                    continue;
                }

                if (tokens.Length >= 3 && tokens[0] == "element")
                {
                    inVertexElement = tokens[1] == "vertex";
                    if (inVertexElement && !int.TryParse(tokens[2], out vertexCount))
                        throw new IOException($"PLY {filePath} header read error: invalid vertex count '{tokens[2]}'");
                    continue;
                }

                if (inVertexElement && tokens.Length >= 3 && tokens[0] == "property")
                {
                    if (tokens[1] == "list")
                        throw new IOException($"PLY {filePath} not supported: vertex list properties are not fixed-stride");

                    ElementType type = ParseElementType(tokens[1]);
                    if (type == ElementType.None)
                    {
                        throw new IOException($"PLY {filePath} not supported: unknown vertex property type '{tokens[1]}'");
                    }

                    vertexStride += TypeToSize(type);
                    attrs.Add((tokens[2], type));
                }
            }

            if (!gotEndHeader)
            {
                throw new IOException($"PLY {filePath} header read error: end_header not found");
            }

            if (!got_binary_le)
            {
                throw new IOException($"PLY {filePath} not supported: needs to be binary, little endian PLY format");
            }
        }

        public static FileStream OpenDataStream(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs)
        {
            var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, fs);
            return fs;
        }

        public static void ReadFile(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs, out NativeArray<byte> vertices)
        {
            using var fs = OpenDataStream(filePath, out vertexCount, out vertexStride, out attrs);

            long totalBytes = (long)vertexCount * vertexStride;
            if (totalBytes > int.MaxValue)
                throw new IOException($"PLY {filePath} read error: raw vertex data exceeds NativeArray<byte> size limits; use streamed import instead");

            vertices = new NativeArray<byte>(vertexCount * vertexStride, Allocator.Persistent);
            var readBytes = fs.Read(vertices);
            if (readBytes != vertices.Length)
                throw new IOException($"PLY {filePath} read error, expected {vertices.Length} data bytes got {readBytes}");
        }

        public enum ElementType
        {
            None,
            Char,
            Float,
            Double,
            UChar,
            Short,
            UShort,
            Int,
            UInt
        }

        static ElementType ParseElementType(string type)
        {
            return type switch
            {
                "char" or "int8" => ElementType.Char,
                "uchar" or "uint8" => ElementType.UChar,
                "short" or "int16" => ElementType.Short,
                "ushort" or "uint16" => ElementType.UShort,
                "int" or "int32" => ElementType.Int,
                "uint" or "uint32" => ElementType.UInt,
                "float" or "float32" => ElementType.Float,
                "double" or "float64" => ElementType.Double,
                _ => ElementType.None
            };
        }

        public static int TypeToSize(ElementType t)
        {
            return t switch
            {
                ElementType.None => 0,
                ElementType.Char => 1,
                ElementType.Float => 4,
                ElementType.Double => 8,
                ElementType.UChar => 1,
                ElementType.Short => 2,
                ElementType.UShort => 2,
                ElementType.Int => 4,
                ElementType.UInt => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
            };
        }

        static string ReadLine(FileStream fs)
        {
            var byteBuffer = new List<byte>();
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1 || b == '\n')
                    break;
                byteBuffer.Add((byte)b);
            }
            // if line had CRLF line endings, remove the CR part
            if (byteBuffer.Count > 0 && byteBuffer.Last() == '\r')
                byteBuffer.RemoveAt(byteBuffer.Count-1);
            return Encoding.UTF8.GetString(byteBuffer.ToArray());
        }
    }
}
#endif // UNITY_EDITOR
