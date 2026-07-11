// Ported from tsgo internal/packagejson/{packagejson,expected,jsonvalue,exportsorimports}_test.go
// (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTES:
//  - Go tests unmarshal into ad-hoc structs via reflection; the AOT port has no reflection
//    JSON, so these drive the hand-written Utf8JsonReader path through Fields/JSONValue.Read.
//  - cmpopts.IgnoreUnexported dropped the unexported actualJSONType from the deep-equal;
//    the ported asserts check the exported {Null, Valid, Value} triples directly.

using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Ts.Collections;
using Ts.PackageJson;
using Xunit;

namespace Ts.PackageJsonTests;

public class PackageJsonTests
{
    [Fact]
    public void TestParseDuplicateNames()
    {
        string content = """
        {
            "name": "test-package",
            "name": "test-package",
            "version": "1.0.0"
        }
        """;
        (Fields got, bool ok) = PackageJsonParser.Parse(Encoding.UTF8.GetBytes(content));
        Assert.True(ok);
        Assert.True(got.HeaderFields.Name.Valid);
        Assert.Equal("test-package", got.HeaderFields.Name.Value);
        Assert.True(got.HeaderFields.Version.Valid);
        Assert.Equal("1.0.0", got.HeaderFields.Version.Value);
        Assert.False(got.HeaderFields.Type.IsPresent());
    }

    [Fact]
    public void TestExpected()
    {
        string json = """
        {
            "name": "test",
            "version": 2,
            "types": null
        }
        """;
        (Fields p, bool ok) = PackageJsonParser.Parse(Encoding.UTF8.GetBytes(json));
        Assert.True(ok);

        Assert.True(p.HeaderFields.Name.Valid);
        Assert.Equal("test", p.HeaderFields.Name.Value);

        // number is present but not a string → invalid, value is the "" zero value.
        Assert.False(p.HeaderFields.Version.Valid);
        Assert.Equal("", p.HeaderFields.Version.Value);

        // explicit null → Null true, not valid.
        Assert.True(p.PathFields.Types.Null);
        Assert.False(p.PathFields.Types.Valid);

        // absent field → not present, not valid, not null.
        Assert.False(p.PathFields.Main.Valid);
        Assert.False(p.PathFields.Main.Null);
        Assert.False(p.PathFields.Main.IsPresent());
    }

    [Fact]
    public void TestJSONValue()
    {
        string json = """
        {
            "private": true,
            "false": false,
            "name": "test",
            "version": 2,
            "exports": {
                ".": {
                    "import": "./test.ts",
                    "default": "./test.ts"
                },
                "./test": [
                    "./test1.ts",
                    "./test2.ts",
                    null
                ],
                "./null": null
            },
            "imports": null
        }
        """;
        JSONValue v = ParseJsonValue(json);
        var obj = v.AsObject();

        Assert.Equal(JSONValueType.JSONValueTypeBoolean, obj.GetOrZero("private").Type);
        Assert.Equal(true, obj.GetOrZero("private").Value);

        Assert.Equal(JSONValueType.JSONValueTypeString, obj.GetOrZero("name").Type);
        Assert.Equal("test", obj.GetOrZero("name").Value);

        Assert.Equal(JSONValueType.JSONValueTypeNumber, obj.GetOrZero("version").Type);
        Assert.Equal((double)2, obj.GetOrZero("version").Value);

        JSONValue exports = obj.GetOrZero("exports");
        Assert.Equal(JSONValueType.JSONValueTypeObject, exports.Type);
        Assert.Equal(3, exports.AsObject().Size());
        Assert.Equal(JSONValueType.JSONValueTypeObject, exports.AsObject().GetOrZero(".").Type);
        Assert.Equal("./test.ts", exports.AsObject().GetOrZero(".").AsObject().GetOrZero("import").Value);

        Assert.Equal(JSONValueType.JSONValueTypeArray, exports.AsObject().GetOrZero("./test").Type);
        Assert.Equal(3, exports.AsObject().GetOrZero("./test").AsArray().Count);
        Assert.Equal("./test1.ts", exports.AsObject().GetOrZero("./test").AsArray()[0].Value);
        Assert.Equal("./test2.ts", exports.AsObject().GetOrZero("./test").AsArray()[1].Value);
        Assert.Equal(JSONValueType.JSONValueTypeNull, exports.AsObject().GetOrZero("./test").AsArray()[2].Type);

        Assert.Equal(JSONValueType.JSONValueTypeNull, exports.AsObject().GetOrZero("./null").Type);

        Assert.Equal(JSONValueType.JSONValueTypeNull, obj.GetOrZero("imports").Type);
        Assert.Null(obj.GetOrZero("imports").Value);

        Assert.Equal(JSONValueType.JSONValueTypeNotPresent, obj.GetOrZero("notPresent").Type);
        Assert.Null(obj.GetOrZero("notPresent").Value);
    }

    [Fact]
    public void TestExports()
    {
        string json = """
        {
            "imports": {
                "#foo": {
                    "import": "./foo.ts"
                }
            },
            "exports": {
                ".": {
                    "import": "./test.ts",
                    "default": "./test.ts"
                },
                "./test": [
                    "./test1.ts",
                    "./test2.ts",
                    null
                ],
                "./null": null
            }
        }
        """;
        (Fields e, bool ok) = PackageJsonParser.Parse(Encoding.UTF8.GetBytes(json));
        Assert.True(ok);

        ExportsOrImports exports = e.PathFields.Exports;
        Assert.True(exports.IsSubpaths());
        Assert.Equal(3, exports.AsObject().Size());
        Assert.True(exports.AsObject().GetOrZero(".").IsConditions());
        Assert.Equal(JSONValueType.JSONValueTypeString, exports.AsObject().GetOrZero(".").AsObject().GetOrZero("import").Type);
        Assert.Equal(JSONValueType.JSONValueTypeNull, exports.AsObject().GetOrZero("./test").AsArray()[2].Type);
        Assert.Equal(JSONValueType.JSONValueTypeNull, exports.AsObject().GetOrZero("./null").Type);

        ExportsOrImports imports = e.PathFields.Imports;
        Assert.True(imports.IsImports());
        Assert.Equal(1, imports.AsObject().Size());
        Assert.True(imports.AsObject().GetOrZero("#foo").IsConditions());
        Assert.Equal(JSONValueType.JSONValueTypeString, imports.AsObject().GetOrZero("#foo").AsObject().GetOrZero("import").Type);
    }

    private static JSONValue ParseJsonValue(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return JSONValue.Read(ref reader);
    }
}
