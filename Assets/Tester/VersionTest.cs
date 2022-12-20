using System;
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
            yield return new TestCaseData("1-alpha.1+build", new Version(1, -1, -1, new []{"alpha", "1"}, "build"));
            yield return new TestCaseData("1.2-alpha.1+build", new Version(1, 2, -1, new []{"alpha", "1"}, "build"));
            yield return new TestCaseData("1.2.3-alpha.1+build", new Version(1, 2, 3, new []{"alpha", "1"}, "build"));
        }

        [Test, TestCaseSource("ParseSource")]
        // ReSharper disable once NUnit.NonPublicMethodWithTestAttribute
        public void ParseAndToString(String parse, object versionIn)
        {
            Version expect = (Version)versionIn;
            Assert.That(Version.TryParse(parse, out var parsed));
            Assert.That(parsed, Is.EqualTo(expect));
            Assert.That(parsed.ToString(), Is.EqualTo(parse));
        }
    }
}
