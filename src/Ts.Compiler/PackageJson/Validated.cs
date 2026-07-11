// Ported from tsgo internal/packagejson/validated.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

namespace Ts.PackageJson;

public interface TypeValidatedField
{
    bool IsPresent();
    bool IsValid();
    string ExpectedJSONType();
    string ActualJSONType();
}
