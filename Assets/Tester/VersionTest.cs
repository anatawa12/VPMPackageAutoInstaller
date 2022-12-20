using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Anatawa12.AutoPackageInstaller
{
    public class VersionTest
    {
        private static IEnumerable<TestCaseData> ParseSource()
        {
            // no prerelease
            yield return new TestCaseData("1", new Version(1, -1, -1));
            yield return new TestCaseData("1.2", new Version(1, 2, -1));
            yield return new TestCaseData("1.2.3", new Version(1, 2, 3));
            // with prerelease
            yield return new TestCaseData("1-alpha.1", new Version(1, -1, -1, "alpha", "1"));
            yield return new TestCaseData("1.2-alpha.1", new Version(1, 2, -1, "alpha", "1"));
            yield return new TestCaseData("1.2.3-alpha.1", new Version(1, 2, 3, "alpha", "1"));
            // with build
            yield return new TestCaseData("1+build", new Version(1, -1, -1, Array.Empty<string>(), "build"));
            yield return new TestCaseData("1.2+build", new Version(1, 2, -1, Array.Empty<string>(), "build"));
            yield return new TestCaseData("1.2.3+build", new Version(1, 2, 3, Array.Empty<string>(), "build"));
            // with both prerelease and build
            yield return new TestCaseData("1-alpha.1+build", new Version(1, -1, -1, new[] { "alpha", "1" }, "build"));
            yield return new TestCaseData("1.2-alpha.1+build", new Version(1, 2, -1, new[] { "alpha", "1" }, "build"));
            yield return new TestCaseData("1.2.3-alpha.1+build", new Version(1, 2, 3, new[] { "alpha", "1" }, "build"));
        }

        [Test, TestCaseSource(nameof(ParseSource))]
        public void ParseAndToString(String parse, object versionIn)
        {
            var expect = (Version)versionIn;
            Assert.That(Version.TryParse(parse, out var parsed));
            Assert.That(parsed, Is.EqualTo(expect));
            Assert.That(parsed.ToString(), Is.EqualTo(parse));
        }

        private static Version Parse(string parse)
        {
            Assert.That(Version.TryParse(parse, out var result));
            return result;
        }

        private static readonly Version[][] OrderedVersions =
        {
            new[] { Parse("1.0.0-alpha") },
            new[] { Parse("1.0.0-alpha.1") },
            new[] { Parse("1.0.0-alpha.11") },
            new[] { Parse("1.0.0-alpha.1b") },
            new[] { Parse("1.0.0-alpha.beta") },
            new[] { Parse("1.0.0-beta") },
            new[] { Parse("1.0.0-beta.2") },
            new[] { Parse("1.0.0-beta.11") },
            new[] { Parse("1.0.0-rc.1") },
            new[] { Parse("1.0.0"), Parse("1.0"), Parse("1") },
            new[] { Parse("1.1.0"), Parse("1.1") },
            new[] { Parse("1.9.0") },
            new[] { Parse("1.10.0") },
            new[] { Parse("1.11.0") },
            new[] { Parse("2.0.0"), Parse("2.0"), Parse("2") },
        };

        private static IEnumerable<TestCaseData> CompareDifferSource()
        {
            for (var i = 0; i < OrderedVersions.Length; i++)
            {
                var lesser = OrderedVersions[i];
                for (var j = i + 1; j < OrderedVersions.Length; j++)
                {
                    var greater = OrderedVersions[j];
                    foreach (var lesserVersion in lesser)
                    foreach (var greaterVersion in greater)
                    {
                        var lesserWithBuild = Parse($"{lesserVersion}+build");
                        var greaterWithBuild = Parse($"{greaterVersion}+build");
                        yield return new TestCaseData(lesserVersion, greaterVersion);
                        yield return new TestCaseData(lesserWithBuild, greaterVersion);
                        yield return new TestCaseData(lesserVersion, greaterWithBuild);
                        yield return new TestCaseData(lesserWithBuild, greaterWithBuild);
                    }
                }
            }
        }

        [Test, TestCaseSource(nameof(CompareDifferSource))]
        public void CompareDifferTest(object lesserIn, object greaterIn)
        {
            var lesser = (Version)lesserIn;
            var greater = (Version)greaterIn;
            Assert.That(lesser, Is.LessThan(greater));
            Assert.That(greater, Is.GreaterThan(lesser));
        }


        private static IEnumerable<TestCaseData> CompareSameSource()
        {
            foreach (var versions in OrderedVersions)
            {
                for (var i = 0; i < versions.Length; i++)
                for (var j = i; j < versions.Length; j++)
                {
                    var leftVersion = versions[i];
                    var rightVersion = versions[j]; 
                    var leftWithBuild = Parse($"{leftVersion}+build");
                    var rightWithBuild = Parse($"{rightVersion}+build");
                    yield return new TestCaseData(leftVersion, rightVersion);
                    yield return new TestCaseData(leftWithBuild, rightVersion);
                    yield return new TestCaseData(leftVersion, rightWithBuild);
                    yield return new TestCaseData(leftWithBuild, rightWithBuild);
                }
            }
        }

        [Test, TestCaseSource(nameof(CompareSameSource))]
        public void CompareSameTest(object aIn, object bIn)
        {
            var a = (Version)aIn;
            var b = (Version)bIn;
            Assert.That(a, Is.EqualTo(b).Using<Version>(Comparer<Version>.Default));
            Assert.That(a, Is.EqualTo(b).Using<Version>(Comparer<Version>.Default));
        }

    }
}
