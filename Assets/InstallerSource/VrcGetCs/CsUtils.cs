// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SemanticVersioning;
using Version = SemanticVersioning.Version;
using Range = SemanticVersioning.Range;
using SystemPath = System.IO.Path;

namespace Anatawa12.VrcGet
{
    /// <summary> represents &amp;std::path::Path or std::path::PathBuf </summary>
    internal sealed class Path : IEquatable<Path>
    {
        [NotNull] private readonly string value;

        public Path([NotNull] string value) => this.value = value ?? throw new ArgumentNullException(nameof(value));

        public Path join(Path segment) => new Path(SystemPath.Combine(value, segment.value));
        public Path joined(Path segment) => join(segment);
        
        public Path join(string segment) => new Path(SystemPath.Combine(value, segment));
        public Path joined(string segment) => join(segment);

        [CanBeNull]
        public Path parent()
        {
            var parent = SystemPath.GetDirectoryName(value);
            return parent == null ? null : new Path(parent);
        }

        public Path strip_prefix(Path prefix)
        {
            // this is not complete implementation but this works in most case
            if (!value.StartsWith(prefix.AsString, StringComparison.Ordinal)) return null;
            var stripped = value.Substring(prefix.AsString.Length);
            var slashes = 0;
            while (slashes < stripped.Length &&
                   (stripped[slashes] == SystemPath.DirectorySeparatorChar ||
                    stripped[slashes] == SystemPath.AltDirectorySeparatorChar))
                slashes++;
            if (slashes != 0) stripped = stripped.Substring(slashes);
            return new Path(stripped);
        }

        public Path with_extension(string extension) => new Path(SystemPath.ChangeExtension(value, extension));

        public bool has_root() => SystemPath.IsPathRooted(value);

        public override string ToString() => value;
        public string AsString => value;
        public bool Equals(Path other) =>
            !ReferenceEquals(null, other) && (ReferenceEquals(this, other) || value == other.value);
        public static bool operator ==(Path left, Path right) => Equals(left, right);
        public static bool operator !=(Path left, Path right) => !Equals(left, right);
        public override bool Equals(object obj) => ReferenceEquals(this, obj) || obj is Path other && Equals(other);
        public override int GetHashCode() => value.GetHashCode();
    }

    // I use this class to keep original range representation
    internal sealed class VersionRange
    {
        private readonly Range _range;
        [NotNull] private readonly string _original;

        private VersionRange([NotNull] string range)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            _range = Range.Parse(range);
            _original = range;
        }

        public static VersionRange Parse(string range) => new VersionRange(range);

        public bool matches(Version installed) => _range.IsSatisfied(installed);

        public bool matches(Version installed, bool includePrerelease) =>
            _range.IsSatisfied(installed, includePrerelease);

        public bool match_pre(Version installed, bool includePrerelease) => matches(installed, includePrerelease);

        private bool Equals(VersionRange other) => _original == other._original;

        public override bool Equals(object obj) =>
            ReferenceEquals(this, obj) || obj is VersionRange other && Equals(other);

        public override int GetHashCode() => _original.GetHashCode();

        public override string ToString() => _original;

        public static VersionRange same_or_later(Version depVersion)
        {
            return new VersionRange($">={depVersion}");
        }

        public bool contains_pre() => ToString().Contains('-');
    }

    internal sealed class DependencyRange
    {
        [NotNull] private readonly VersionRange _original;

        private DependencyRange([NotNull] VersionRange version) => _original = version ?? throw new ArgumentNullException(nameof(version));
        public DependencyRange(Version version) => _original = VersionRange.Parse(version.ToString());

        public static DependencyRange Parse(string get) => new DependencyRange(VersionRange.Parse(get));
        public override bool Equals(object obj) => ReferenceEquals(this, obj) || obj is DependencyRange other && _original.Equals(other._original);
        public override int GetHashCode() => _original.GetHashCode();
        public override string ToString() => _original.ToString();

        [CanBeNull]
        public Version as_single_version() => Version.TryParse(_original.ToString(), out var parsed) ? parsed : null;
        public bool matches(Version version)
        {
            if (as_single_version() is Version single)
                return single <= version;
            else
                return _original.match_pre(version, true);
        }
        public VersionRange as_range() => as_single_version() is Version version ? VersionRange.same_or_later(version) : _original;
    }

    static class CsUtils
    {
        public static bool split_once(this string input, char separator, out string left, out string right)
        {
            var index = input.IndexOf(separator);
            if (index == -1)
            {
                left = null;
                right = null;
                return false;
            }

            left = input.Substring(0, index);
            right = input.Substring(index + 1);
            return true;
        }

        public static TValue entry_or_default<TKey, TValue>(this Dictionary<TKey, TValue> self, TKey key) where TValue : new()
        {
            if (!self.TryGetValue(key, out var value))
                self.Add(key, value = new TValue());
            return value;
        }

        public static void insert<TKey, TValue>(this Dictionary<TKey, TValue> self, TKey key, TValue value) => self[key] = value;

        public static void insert<T>(this HashSet<T> self, T value) => self.Add(value);

        public static void push<T>(this List<T> self, T value) => self.Add(value);

        public static T replace<T>(ref T place, T value)
        {
            var old = place;
            place = value;
            return old;
        }

        public static bool is_pre(this Version version) => version.IsPreRelease;


        public static async Task WriteAllText(Path path, string content) =>
            await Task.Run(() => File.WriteAllText(path.AsString, content));
        public static async Task create_dir_all(Path path) =>
            await Task.Run(() => Directory.CreateDirectory(path.AsString));
        public static async Task remove_dir_all(Path path) =>
            await Task.Run(() => Directory.Delete(path.AsString, true));
        public static async Task remove_file(Path path) =>
            await Task.Run(() => File.Delete(path.AsString));

        public static void AddAllTo<T>(this IEnumerable<T> self, List<T> collection)
        {
            collection.AddRange(self);
        }

        public static void AddAllTo<T>(this IEnumerable<T> self, HashSet<T> collection)
        {
            foreach (var value in self)
                collection.Add(value);
        }

        public static int len<T>(this ICollection<T> self) => self.Count;
        public static void retain<T>(this List<T> self, Predicate<T> match) => self.RemoveAll(x => !match(x));

        public static void retain<T>(this LinkedList<T> self, Predicate<T> match)
        {
            var iter = self.First;
            while (iter != null)
            {
                var current = iter;
                var value = iter.Value;
                iter = iter.Next;
                if (!match(value))
                    self.Remove(current);
            }
        }

        public static T? pop_back<T>(this LinkedList<T> self) where T : struct
        {
            var result = self.First?.Value;
            if (result != null) self.RemoveFirst();
            return result;
        }

        public static void push_back<T>(this LinkedList<T> self, T value) => self.AddLast(value);

        [CanBeNull]
        public static TValue get<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> self, TKey key)
        {
            self.TryGetValue(key, out var result);
            return result;
        }

        public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> self, Func<T, TKey> keySelector)
        {
            return self.Distinct(new KeyEqualityComparer<T, TKey>(keySelector));
        }

        public static async Task ReadExactAsync(this Stream stream, byte[] buffer)
        {
            int offset = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, offset, buffer.Length - offset)) != 0)
            {
                offset += read;
                if (offset == buffer.Length) return;
            }

            throw new EndOfStreamException("Unexpected EOS");
        }

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> self, out TKey key,
            out TValue value) => (key, value) = (self.Key, self.Value);

        // simplified MaxBy from dotnet 7
        public static TSource MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        {
            var comparer = Comparer<TKey>.Default;

            using (IEnumerator<TSource> e = source.GetEnumerator())
            {
                if (!e.MoveNext())
                {
                    return default;
                }

                var value = e.Current;
                var key = keySelector(value);

                while (e.MoveNext())
                {
                    var nextValue = e.Current;
                    var nextKey = keySelector(nextValue);

                    if (comparer.Compare(nextKey, key) <= 0) continue;

                    key = nextKey;
                    value = nextValue;
                }

                return value;
            }
        }
    }

    internal class KeyEqualityComparer<T, TKey> : IEqualityComparer<T>
    {
        private readonly Func<T, TKey> _keySelector;

        public KeyEqualityComparer(Func<T, TKey> keySelector)
        {
            _keySelector = keySelector;
        }

        public bool Equals(T x, T y) => _keySelector(x).Equals(_keySelector(y));

        public int GetHashCode(T obj) => _keySelector(obj).GetHashCode();
    }

    internal class VrcGetException : Exception
    {
        public VrcGetException(string message) : base(message)
        {
        }

        public VrcGetException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    internal class OfflineModeException : VrcGetException
    {
        public OfflineModeException() : base("Offline Mode") {}
    }
}
