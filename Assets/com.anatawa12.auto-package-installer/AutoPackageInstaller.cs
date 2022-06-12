/*
 * This is a part of https://github.com/anatawa12/AutoPackageInstaller.
 * 
 * MIT License
 * 
 * Copyright (c) 2022 anatawa12
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;

namespace Anatawa12.AutoPackageInstaller
{

    [InitializeOnLoad]
    public class AutoPackageInstaller
    {
        private const string TesterGuid = "40f4470be29a43a792b7c41576813ea3";
        private const string ConfigGuid = "9028b92d14f444e2b8c389be130d573f";
        private const string ManifestPath = "Packages/manifest.json";

        private static readonly string[] ToBeRemoved = 
        {
            ConfigGuid,
            // the C# file
            "30732659753784f469c8c521aa469152",
            // the asmdef file
            "f7306773db58a40f2b8c5b6ed99db57b",
            // the folder
            "4b344df74d4849e3b2c978b959abd31b",
        };

        static AutoPackageInstaller()
        {
            var testerExist = !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(TesterGuid));
            if (testerExist)
            {
                Debug.Log("Tester found. skipping auto install & remove self");
                return;
            }
            DoInstall();
            RemoveSelf();
        }

        public AutoPackageInstaller()
        {
        }

        public static void DoInstall()
        {
            var configJson = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            if (string.IsNullOrEmpty(configJson))
            {
                EditorUtility.DisplayDialog("ERROR", "config.json not found. installing package failed.", "ok");
                return;
            }

            var config = new JsonParser(File.ReadAllText(configJson, Encoding.UTF8)).Parse(JsonType.Obj);
            var manifest = new JsonParser(File.ReadAllText(ManifestPath, Encoding.UTF8)).Parse(JsonType.Obj);

            var updates = new List<(string key, string value)>();
            var dependencies = config.Get("dependencies", JsonType.Obj);
            var manifestDependencies = manifest.GetOrPut("dependencies", () => new JsonObj(), JsonType.Obj);
            foreach (var key in dependencies.Keys)
            {
                var value = dependencies.Get(key, JsonType.String);
                var version = manifestDependencies.Get(key, JsonType.String, true);
                if (version == null || ShouldUpdatePackage(version, value))
                    updates.Add((key, value));
            }

            if (!EditorUtility.DisplayDialog("Confirm", "You're installing the following packages:\n"
                     + string.Join("\n", updates.Select((p) => $"{p.key} version {p.value}")), 
                    "Install", "Cancel"))
                return;

            foreach (var (key, value) in updates)
                manifestDependencies.Put(key, value, JsonType.String);

            try
            {
                File.Copy(ManifestPath, ManifestPath + ".bak", true);
            }
            catch (IOException e)
            {
                Debug.LogError($"error during creating backup: {e}");
            }

            File.WriteAllText(ManifestPath, JsonWriter.Write(manifest));
            SetDirty(ManifestPath);
        }

        private static bool ShouldUpdatePackage(string current, string value)
        {
            try
            {
                Version currentVersion = new Version(current);
                Version valueVersion = new Version(value);
                return currentVersion.CompareTo(valueVersion) < 0;
            }
            catch (Exception e)
            {
                // non-semver
                return true;
            }
        }

        private readonly struct Version : IComparable<Version>
        {
            public readonly int Maj;
            public readonly int Min;
            public readonly int Pat;
            public readonly string Pre;
            public readonly string Build;

            public Version(string version)
            {
                Maj = 0;
                Min = 0;
                Pat = 0;
                Pre = null;
                Build = null;

                var split = version.Split(new[] { '+' }, 2);
                if (split.Length == 2) (version, Build) = (split[0], split[1]);
                split = version.Split(new[] { '-' }, 2);
                if (split.Length == 2) (version, Pre) = (split[0], split[1]);
                split = version.Split(new[] { '.' }, 3);

                switch (split.Length)
                {
                    default:
                        Pat = int.Parse(split[2]);
                        goto case 2;
                    case 2:
                        Min = int.Parse(split[1]);
                        goto case 1;
                    case 1:
                        Maj = int.Parse(split[0]);
                        break;
                }
            }


            public int CompareTo(Version other)
            {
                var majComparison = Maj.CompareTo(other.Maj);
                if (majComparison != 0) return majComparison;
                var minComparison = Min.CompareTo(other.Min);
                if (minComparison != 0) return minComparison;
                var patComparison = Pat.CompareTo(other.Pat);
                if (patComparison != 0) return patComparison;
                var preComparison = string.Compare(Pre, other.Pre, StringComparison.Ordinal);
                if (preComparison != 0) return preComparison;
                return string.Compare(Build, other.Build, StringComparison.Ordinal);
            }
        }

        public static void RemoveSelf()
        {
            foreach (var remove in ToBeRemoved)
            {
                RemoveFileAsset(remove);
            }
        }

        private static void RemoveFileAsset(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (File.Exists(path))
            {
                SetDirty(path);
                try
                {
                    File.Delete(path);
                    File.Delete(path + ".meta");
                }
                catch (IOException e)
                {
                    Debug.LogError($"error removing installer: {e}");
                }
            }
            else if (Directory.Exists(path))
            {
                SetDirty(path);
                try
                {
                    Directory.Delete(path);
                    File.Delete(path + ".meta");
                }
                catch (IOException e)
                {
                    Debug.LogError($"error removing installer: {e}");
                }
            }
        }

        private static void SetDirty(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) EditorUtility.SetDirty(asset);
        }
    }

    // minimum json parser with JsonObj, List<object>, string, long, double, bool, and null
    // This doesn't use Dictionary because it can't save order
    #region Json

    class JsonObj
    {
        [NotNull] internal readonly List<(string, object)> Obj = new List<(string, object)>();

        public JsonObj()
        {
        }

        public int Count => Obj.Count;
        public IEnumerable<string> Keys => Obj.Select(pair => pair.Item1);

        public void Add(string key, object value)
        {
            Obj.Add((key, value));
        }

        public List<(string, object)>.Enumerator GetEnumerator()
        {
            return Obj.GetEnumerator();
        }

        public T Get<T>(string key, TypeDesc<T> typeDesc, bool optional = false)
        {
            return typeDesc.Cast(Obj.FirstOrDefault(p => p.Item1 == key).Item2, optional);
        }

        public T GetOrPut<T>(string key, Func<T> getDefault, TypeDesc<T> typeDesc)
        {
            var pair = Obj.FirstOrDefault(p => p.Item1 == key);
            if (pair.Item1 != null) return typeDesc.Cast(pair.Item2);
            T value = getDefault();
            Put(key, value, typeDesc);
            return value;
        }

        public void Put<T>(string key, object value, TypeDesc<T> typeDesc)
        {
            for (int i = 0; i < Obj.Count; i++)
            {
                if (Obj[i].Item1 == key)
                {
                    var pair = Obj[i];
                    pair.Item2 = value;
                    Obj[i] = pair;
                    return;
                }
            }

            Add(key, value);
        }
    }

    static class JsonType
    {
#pragma warning disable CS0414
        public static readonly TypeDesc<JsonObj> Obj = default;
        public static readonly TypeDesc<List<object>> List = default;
        public static readonly TypeDesc<string> String = default;
        public static readonly TypeDesc<double> Number = default;
        public static readonly TypeDesc<bool> Bool = default;
        public static readonly TypeDesc<object> Any = default;
#pragma warning restore CS0414
    }

    // dummy struct for generic
    readonly struct TypeDesc<T>
    {
        public T Cast(object value, bool optional = false)
        {
            if (new object() is T) return (T)value;
            return value == null && optional ? default
                : value is T t ? t 
                : throw new InvalidOperationException($"unexpected type: {value?.GetType()?.ToString() ?? "null"}");
        }
    }

    class JsonParser
    {
        enum TokenType : sbyte
        {
            None,
            // {}
            OpenBrace,
            CloseBrace,
            // []
            OpenBracket,
            CloseBracket,
            Comma,
            Colon,
            Literal,
        }

        private readonly string _input;
        private int _cursor;

        public JsonParser(string input)
        {
            _input = input;
            _cursor = 0;
        }

        public T Parse<T>(TypeDesc<T> typeDesc)
        {
            var result = ParseValue();
            CheckEof();
            return typeDesc.Cast(result);
        }

        private object ParseValue()
        {
            var token = NextToken();
            switch (token.Item1)
            {
                case TokenType.Literal:
                    return token.Item2;
                case TokenType.OpenBracket:
                    List<object> list = new List<object>();
                    if ((token = NextToken()).Item1 != TokenType.CloseBracket)
                    {
                        _token = token;
                        list.Add(ParseValue());
                        while ((token = NextToken()).Item1 != TokenType.CloseBracket)
                        {
                            if (token.Item1 != TokenType.Comma)
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");
                            list.Add(ParseValue());
                        }
                    }

                    return list;
                
                case TokenType.OpenBrace:
                    JsonObj dict = new JsonObj();
                    if ((token = NextToken()).Item1 != TokenType.OpenBrace)
                    {
                        if (token.Item1 != TokenType.Literal || !(token.Item2 is string key0))
                            throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");
                        
                        if ((token = NextToken()).Item1 != TokenType.Colon)
                            throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");

                        dict.Add(key0, ParseValue());

                        while ((token = NextToken()).Item1 != TokenType.CloseBrace)
                        {
                            if (token.Item1 != TokenType.Comma)
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");
                            
                            if ((token = NextToken()).Item1 != TokenType.Literal || !(token.Item2 is string key))
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");
                            
                            if ((token = NextToken()).Item1 != TokenType.Colon)
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");

                            dict.Add(key, ParseValue());
                        }
                    }

                    return dict;

                default:
                    throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");
            }
        }

        private (TokenType, object) _token;

        private (TokenType, object) NextToken()
        {
            if (_token.Item1 != TokenType.None)
            {
                var result = _token;
                _token = default;
                return result;
            }

            return ComputeNextToken();
        }

        private (TokenType, object) ComputeNextToken()
        {
            char c;
            while ((c = GetMoveChar()) == '\u0020' || c == '\u000A' || c == '\u000D' || c == '\u0009')
            {
            }
            // now c is first non-whitespace char

            switch (c)
            {
                case '{': return (TokenType.OpenBrace, null);
                case '}': return (TokenType.CloseBrace, null);
                case '[': return (TokenType.OpenBracket, null);
                case ']': return (TokenType.CloseBracket, null);
                case ',': return (TokenType.Comma, null);
                case ':': return (TokenType.Colon, null);

                // keyword literals
                case 't':
                    if (GetMoveChar() != 'r' || GetMoveChar() != 'u' || GetMoveChar() != 'e')
                        throw new InvalidOperationException("invalid json: unknown token starting 't'");
                    return (TokenType.Literal, true);
                case 'f':
                    if (GetMoveChar() != 'a' || GetMoveChar() != 'l' || GetMoveChar() != 's' || GetMoveChar() != 'e')
                        throw new InvalidOperationException("invalid json: unknown token starting 'f'");
                    return (TokenType.Literal, false);
                case 'n':
                    if (GetMoveChar() != 'u' || GetMoveChar() != 'l' || GetMoveChar() != 'l')
                        throw new InvalidOperationException("invalid json: unknown token starting 'n'");
                    return (TokenType.Literal, null);

                // string literal
                case '"': return (TokenType.Literal, StringLiteral());

                // numeric literal
                case '-': return (TokenType.Literal, NumericLiteral(c));
                case '+': return (TokenType.Literal, NumericLiteral(c));
                case '0': return (TokenType.Literal, 0);
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    return (TokenType.Literal, NumericLiteral(c));
                case '\0':
                    throw new InvalidOperationException("invalid json: unexpected eof");
                default:
                    throw new InvalidOperationException(InvalidChar(c));
            }
        }

        private string StringLiteral()
        {
            StringBuilder builder = new StringBuilder();
            char c;
            while ((c = GetMoveChar()) != '"')
            {
                if (c == '\\')
                {
                    switch (c = GetMoveChar())
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            c = (char)0;
                            c |= (char)(HexChar() << 24);
                            c |= (char)(HexChar() << 16);
                            c |= (char)(HexChar() << 8);
                            c |= (char)(HexChar() << 0);
                            builder.Append(c);
                            break;
                        default:
                            throw new InvalidOperationException(InvalidChar(c));
                    }
                }
                else if ('\u0020' <= c)
                {
                    builder.Append(c);
                }
                else
                {
                    throw new InvalidOperationException(InvalidChar(c));
                }
            }

            return builder.ToString();
        }

        private int HexChar()
        {
            var c = GetMoveChar();
            if ('0' <= c && c <= '9')
                return c - '0';
            if ('a' <= c && c <= 'f')
                return c - 'a' + 10;
            if ('A' <= c && c <= 'F')
                return c - 'A' + 10;
            throw new InvalidOperationException(InvalidChar(c));
        }

        private static string InvalidChar(char c)
        {
            return $"invalid json: invalid char: '{c}' ({(int)c:x2})";
        }

        private void CheckEof()
        {
            char c;
            while (char.IsWhiteSpace(c = GetMoveChar()))
            {
            }
            if (c != '\0')
                throw new InvalidOperationException(InvalidChar(c));
        }

        private object NumericLiteral(char c)
        {
            if (c == '+' || c == '-')
                c = GetMoveChar();

            var start = _cursor - 1;
            c = SkipIntegerLiteral(c);

            if (c == '.')
            {
                c = SkipIntegerLiteral(GetMoveChar());
            }

            if (c == 'e')
            {
                if ((c = GetMoveChar()) == '+' || c == '-')
                    c = GetMoveChar();
                c = SkipIntegerLiteral(c);
            }

            _cursor--;
            var str = _input.Substring(start, _cursor);

            //if (long.TryParse(str, out var l)) return l;
            if (double.TryParse(str, out var d)) return d;
            throw new InvalidOperationException($"invalid json: invalid number: {str}");
        }

        private char SkipIntegerLiteral(char c)
        {
            long integer = 0;
            while (true)
            {
                if ('0' <= c && c <= '9')
                {
                    if (integer >= long.MaxValue / 10)
                        throw new InvalidOperationException("invalid json: number too big");
                    integer = integer * 10 + (c - '0');
                }
                else
                {
                    return c;
                }

                c = GetMoveChar();
            }
        }

        private char GetMoveChar()
        {
            var cur = _cursor;
            if (cur >= _input.Length) return '\0';
            _cursor = cur + 1;
            return _input[cur];
        }
    }

    static class JsonWriter
    {
        public static string Write(object obj)
        {
            StringBuilder builder = new StringBuilder();
            WriteToBuilder(obj, builder, "");
            builder.Append(Environment.NewLine);
            return builder.ToString();
        }

        private static void WriteToBuilder(object o, StringBuilder builder, string indent)
        {
            if (o == null) builder.Append("null");
            else if (o is string s) WriteString(builder, s);
            //else if (o is long l) builder.Append(l);
            else if (o is double d) builder.Append(d);
            else if (o is bool b) builder.Append(b);
            else if (o is JsonObj dict) WriteObject(builder, dict, indent);
            else if (o is List<object> list) WriteArray(builder, list, indent);
            else throw new ArgumentException($"unsupported type: {o.GetType()}", nameof(o));
        }

        private static void WriteString(StringBuilder builder, string s)
        {
            builder.Append('"');
            foreach (var c in s)
            {
                if (c == '"') builder.Append("\\\"");
                else if (c == '\\') builder.Append("\\\\");
                else if (c < '\u0020') builder.Append($"'\\u{(int)c:x4}'");
                else builder.Append(c);
            }
            builder.Append('"');
        }

        private static void WriteObject(StringBuilder builder, JsonObj dict, string indent)
        {
            if (dict.Count == 0)
            {
                builder.Append("{}");
                return;
            }


            var oldIndent = indent;
            builder.Append('{').Append(Environment.NewLine);
            indent += "  ";
            using (List<(string, object)>.Enumerator e = dict.GetEnumerator())
            {
                e.MoveNext();
                while (true)
                {
                    var pair = e.Current;
                    var hasNext = e.MoveNext();
                    builder.Append(indent);
                    WriteString(builder, pair.Item1);
                    builder.Append(": ");
                    WriteToBuilder(pair.Item2, builder, indent);
                    if (hasNext)
                        builder.Append(',');
                    builder.Append(Environment.NewLine);
                    if (!hasNext) break;
                }
            }
            
            builder.Append(oldIndent).Append('}');
        }

        private static void WriteArray(StringBuilder builder, List<object> list, string indent)
        {
            if (list.Count == 0)
            {
                builder.Append("[]");
                return;
            }


            var oldIndent = indent;
            builder.Append('[').Append(Environment.NewLine);
            indent += "  ";
            using (List<object>.Enumerator e = list.GetEnumerator())
            {
                e.MoveNext();
                while (true)
                {
                    var value = e.Current;
                    var hasNext = e.MoveNext();
                    builder.Append(indent);
                    WriteToBuilder(value, builder, indent);
                    if (hasNext)
                        builder.Append(',');
                    builder.Append(Environment.NewLine);
                    if (!hasNext) break;
                }
            }
            
            builder.Append(oldIndent).Append(']');
        }
    }
    #endregion
}
