using Anatawa12.VrcGet;
using static Anatawa12.VrcGet.ReleaseType;
using NUnit.Framework;
using UnityEngine;

// ReSharper disable InconsistentNaming

namespace Anatawa12.VpmPackageAutoInstaller
{
    public class UnityVersionTest
    {
        [Test]
        public void parse_unity_version()
        {
            void good(string input, ushort major, byte minor, byte revision, ReleaseType type, byte increment)
            {
                var version = UnityVersion.parse(input);
                Assert.NotNull(version);
                Assert.AreEqual(major, version.major());
                Assert.AreEqual(minor, version.minor());
                Assert.AreEqual(revision, version.revision());
                Assert.AreEqual(type, version.type());
                Assert.AreEqual(increment, version.increment());
            }

            void bad(string input)
            {
                var version = UnityVersion.parse(input);
                Assert.Null(version);
            }

            good("5.6.6f1", 5, 6, 6, Normal, 1);

            good("2019.1.0a1", 2019, 1, 0, Alpha, 1);
            good("2019.1.0b1", 2019, 1, 0, Beta, 1);
            good("2019.4.31f1", 2019, 4, 31, Normal, 1);
            good("2023.3.6f1", 2023, 3, 6, Normal, 1);
            good("2023.3.6c1", 2023, 3, 6, China, 1);
            good("2023.3.6p1", 2023, 3, 6, Patch, 1);
            good("2023.3.6x1", 2023, 3, 6, Experimental, 1);

            good("2019.1.0a1-EXTRA", 2019, 1, 0, Alpha, 1);
            
            good(Application.unityVersion, 2019, 4, 31, Normal, 1); // VPAI

            bad("2022");
            bad("2019.0");
            bad("5.6.6");
            bad("2023.4.6f");
        }

        [Test]
        public void ord_version()
        {
            void test(string left, string right)
            {
                var leftVersion = UnityVersion.parse(left);
                var rightVersion = UnityVersion.parse(right);
                Assert.NotNull(leftVersion);
                Assert.NotNull(rightVersion);
                Assert.That(leftVersion < rightVersion);
                Assert.That(rightVersion > leftVersion);
            }

            test("5.6.5f1", "5.6.6f1");
            test("5.6.6f1", "5.6.6f2");
            test("5.6.6f1", "2022.1.0f1");
            test("2022.1.0a1", "2022.1.0f1");

            Assert.That(UnityVersion.parse("2022.1.0f1").CompareTo(UnityVersion.parse("2022.1.0c1")) == 0);
        }


        [Test]
        public void ord_release_type()
        {
            var Equal = 0;
            var Less = -1;
            var Greater = 1;

            void test(ReleaseType left, ReleaseType right, int ordering)
            {
                Assert.AreEqual(ordering, left.CompareTo1(right));
            }

            Assert.That(Alpha.CompareTo1(Beta) < 0);
            Assert.That(Beta.CompareTo1(Normal) < 0);
            Assert.That(Beta.CompareTo1(China) < 0);
            Assert.That(China.CompareTo1(Patch) < 0);
            Assert.That(Patch.CompareTo1(Experimental) < 0);

            test(Alpha, Alpha, Equal);
            test(Alpha, Beta, Less);
            test(Alpha, Normal, Less);
            test(Alpha, China, Less);
            test(Alpha, Patch, Less);
            test(Alpha, Experimental, Less);

            test(Beta, Alpha, Greater);
            test(Beta, Beta, Equal);
            test(Beta, Normal, Less);
            test(Beta, China, Less);
            test(Beta, Patch, Less);
            test(Beta, Experimental, Less);

            test(Normal, Alpha, Greater);
            test(Normal, Beta, Greater);
            test(Normal, Normal, Equal);
            test(Normal, China, Equal);
            test(Normal, Patch, Less);
            test(Normal, Experimental, Less);

            test(China, Alpha, Greater);
            test(China, Beta, Greater);
            test(China, Normal, Equal);
            test(China, China, Equal);
            test(China, Patch, Less);
            test(China, Experimental, Less);

            test(Patch, Alpha, Greater);
            test(Patch, Beta, Greater);
            test(Patch, Normal, Greater);
            test(Patch, China, Greater);
            test(Patch, Patch, Equal);
            test(Patch, Experimental, Less);

            test(Experimental, Alpha, Greater);
            test(Experimental, Beta, Greater);
            test(Experimental, Normal, Greater);
            test(Experimental, China, Greater);
            test(Experimental, Patch, Greater);
            test(Experimental, Experimental, Equal);
        }
    }
}