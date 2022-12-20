using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Anatawa12.AutoPackageInstaller
{
    public class JsonTest
    {
        private static IEnumerable<TestCaseData> ParseAndSerializePairs()
        {
            // simple literals
            yield return new TestCaseData("{}", new JsonObj());
            yield return new TestCaseData("[]", new List<object>());
            yield return new TestCaseData(@"""simple""", "simple");
            yield return new TestCaseData(@"""\""\\""", "\"\\");
            yield return new TestCaseData("0", 0.0);
            yield return new TestCaseData("1", 1.0);
            yield return new TestCaseData("0.5", 0.5);
            yield return new TestCaseData("-0.5", -0.5);
            yield return new TestCaseData("2.3", 2.3);
            yield return new TestCaseData("true", true);
            yield return new TestCaseData("false", false);
            yield return new TestCaseData("null", null);
            // lists
            yield return new TestCaseData(
                "[\n"
                + "  \"str\",\n"
                + "  1,\n"
                + "  false\n"
                + "]", new List<object> { "str", 1.0, false });

            // objects
            yield return new TestCaseData(
                "{\n"
                + "  \"key1\": \"string\",\n"
                + "  \"key2\": 1\n"
                + "}", new JsonObj
                {
                    { "key1", "string" },
                    { "key2", 1.0 },
                });
        }

        [Test, TestCaseSource("ParseAndSerializePairs")]
        public void ParseAndSerialize(String parse, object parsed)
        {
            Assert.That(new JsonParser(parse).Parse(JsonType.Any), Is.EqualTo(parsed));
            Assert.That(JsonWriter.Write(parsed), Is.EqualTo(parse));
        }
    }
}
