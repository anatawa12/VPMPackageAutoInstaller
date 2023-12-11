using System;
using JetBrains.Annotations;

// ReSharper disable LocalVariableHidesMember
// ReSharper disable InconsistentNaming

namespace Anatawa12.VrcGet
{
    internal class UnityVersion : IComparable<UnityVersion>
    {
        private ushort _major;
        private byte _minor;
        private byte _revision;
        private ReleaseType _type;
        private byte _increment;

        public UnityVersion(ushort major, byte minor, byte revision, ReleaseType type, byte increment)
        {
            _major = major;
            _minor = minor;
            _revision = revision;
            _type = type;
            _increment = increment;
        }

        public ushort major() => _major;
        public byte minor() => _minor;
        public byte revision() => _revision;
        public ReleaseType type() => _type;
        public byte increment() => _increment;

        [CanBeNull]
        public static UnityVersion parse(string input)
        {
            string rest;
            if (!input.split_once('.', out var major_str, out rest)) return null;
            if (!ushort.TryParse(major_str, out var major)) return null;
            if (!rest.split_once('.', out var minor_str, out rest)) return null;
            if (!byte.TryParse(minor_str, out var minor)) return null;

            var revision_delimiter = rest.IndexOfAny(new []{ 'a', 'b', 'f', 'c', 'p', 'x' });
            if (revision_delimiter == -1) return null;
            var revision = rest.Substring(0, revision_delimiter);
            if (!byte.TryParse(revision, out var revision_byte)) return null;
            if (!ReleaseTypeExt.TryParse(rest[revision_delimiter], out var type)) return null;
            rest = rest.Substring(revision_delimiter + 1);

            if (!rest.split_once('-', out var increment_str, out var _)) increment_str = rest;
            if (!byte.TryParse(increment_str, out var increment)) return null;
            
            return new UnityVersion(major, minor, revision_byte, type, increment);
        }

        public override string ToString() => $"{major()}.{minor()}.{revision()}{type().ToChar()}{increment()}";

        /*
         * // 1 < 2 < 3 < 4 < 5 < years < 6
fn major_ord(this: u16, other: u16) -> Ordering {
    let this_year = this >= 2000;
    let other_year = other >= 2000;

    match (this_year, other_year) {
        (true, true) => this.cmp(&other),
        (false, false) => this.cmp(&other),
        (true, false) => {
            if other <= 5 {
                Ordering::Greater
            } else {
                Ordering::Less
            }
        }
        (false, true) => {
            if this <= 5 {
                Ordering::Less
            } else {
                Ordering::Greater
            }
        }
    }
}
         */

        public int CompareTo(UnityVersion other)
        {
            var major = major_ord(this.major(), other.major());
            if (major != 0) return major;
            var minor = this.minor().CompareTo(other.minor());
            if (minor != 0) return minor;
            var revision = this.revision().CompareTo(other.revision());
            if (revision != 0) return revision;
            var type = this.type().CompareTo1(other.type());
            if (type != 0) return type;
            return this.increment().CompareTo(other.increment());
        }

        internal static int major_ord(ushort self, ushort other)
        {
            var this_year = self >= 2000;
            var other_year = other >= 2000;

            if (this_year == other_year) return self.CompareTo(other);
            if (this_year) return other <= 5 ? 1 : -1;
            return self <= 5 ? -1 : 1;
        }

        public static bool operator <(UnityVersion left, UnityVersion right) => left.CompareTo(right) < 0;
        public static bool operator >(UnityVersion left, UnityVersion right) => left.CompareTo(right) > 0;
        public static bool operator <=(UnityVersion left, UnityVersion right) => left.CompareTo(right) <= 0;
        public static bool operator >=(UnityVersion left, UnityVersion right) => left.CompareTo(right) >= 0;
    }

    internal enum ReleaseType : byte
    {
        Alpha,
        Beta,
        Normal,
        China,
        Patch,
        Experimental,
    }

    internal static class ReleaseTypeExt
    {
        public static int CompareTo1(this ReleaseType self, ReleaseType other)
        {
            if (self == ReleaseType.Alpha && other == ReleaseType.Alpha) return 0;
            if (self == ReleaseType.Alpha) return -1;
            if (other == ReleaseType.Alpha) return 1;
            
            if (self == ReleaseType.Beta && other == ReleaseType.Beta) return 0;
            if (self == ReleaseType.Beta) return -1;
            if (other == ReleaseType.Beta) return 1;
            
            if (self == ReleaseType.Normal && other == ReleaseType.Normal) return 0;
            if (self == ReleaseType.Normal && other == ReleaseType.China) return 0;
            if (self == ReleaseType.China && other == ReleaseType.Normal) return 0;
            if (self == ReleaseType.China && other == ReleaseType.China) return 0;
            if (self == ReleaseType.Normal) return -1;
            if (self == ReleaseType.China) return -1;
            if (other == ReleaseType.Normal) return 1;
            if (other == ReleaseType.China) return 1;
            
            if (self == ReleaseType.Patch && other == ReleaseType.Patch) return 0;
            if (self == ReleaseType.Patch) return -1;
            if (other == ReleaseType.Patch) return 1;
            
            if (self == ReleaseType.Experimental && other == ReleaseType.Experimental) return 0;

            throw new ArgumentOutOfRangeException(nameof(other), other, null);
        }

        public static char ToChar(this ReleaseType type)
        {
            switch (type)
            {
                case ReleaseType.Alpha:
                    return 'a';
                case ReleaseType.Beta:
                    return 'b';
                case ReleaseType.Normal:
                    return 'f';
                case ReleaseType.China:
                    return 'c';
                case ReleaseType.Patch:
                    return 'p';
                case ReleaseType.Experimental:
                    return 'x';
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public static bool TryParse(char c, out ReleaseType type)
        {
            switch (c)
            {
                case 'a':
                    type = ReleaseType.Alpha;
                    return true;
                case 'b':
                    type = ReleaseType.Beta;
                    return true;
                case 'f':
                    type = ReleaseType.Normal;
                    return true;
                case 'c':
                    type = ReleaseType.China;
                    return true;
                case 'p':
                    type = ReleaseType.Patch;
                    return true;
                case 'x':
                    type = ReleaseType.Experimental;
                    return true;
                default:
                    type = default;
                    return false;
            }
        }
    }
}