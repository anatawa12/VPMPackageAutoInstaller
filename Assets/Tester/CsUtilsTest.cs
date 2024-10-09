using Anatawa12.VrcGet;
using static Anatawa12.VrcGet.ReleaseType;
using NUnit.Framework;
using UnityEngine;

// ReSharper disable InconsistentNaming

namespace Anatawa12.VpmPackageAutoInstaller
{
    public class CsUtilsTest
    {
        [Test]
        public void TestStripPrefix()
        {
            Assert.AreEqual(new Path("Relative/Part"), new Path("C:/Test/Folder/Relative/Part").strip_prefix(new Path("C:/Test/Folder")));
            Assert.AreEqual(new Path("Relative/Part"), new Path("C:/Test/Folder/////Relative/Part").strip_prefix(new Path("C:/Test/Folder")));
            Assert.AreEqual(new Path("Relative/Part"), new Path("C:/Test/Folder/Relative/Part").strip_prefix(new Path("C:/Test/Folder/")));
        }
    }
}