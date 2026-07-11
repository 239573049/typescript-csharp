// Ported from tsgo internal/core/typeacquisition.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;

namespace Ts.Core;

public sealed class TypeAcquisition
{
    public Tristate Enable;
    public string[]? Include;
    public string[]? Exclude;
    public Tristate DisableFilenameBasedTypeAcquisition;

    // PORT-NOTE: instance method rather than an extension — object.Equals(object) would
    // always win overload resolution over an extension named Equals, silently degrading to
    // reference equality (same reasoning as Collections/Set.Equals). Unlike Go, a null
    // receiver throws here (Go: nil.Equals(nil) == true, nil.Equals(x) == false); the ata.go
    // call site (ti.TypeAcquisition.Equals(...), ported in phase 8) uses a possibly-nil
    // receiver and must guard null explicitly. Registered in the phase report as a known
    // deviation. slices.Equal (nil == empty) maps to span SequenceEqual, which uses ordinal
    // string equality.
    public bool Equals(TypeAcquisition? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (other is null)
        {
            return false;
        }

        return Enable == other.Enable &&
            Include.AsSpan().SequenceEqual(other.Include.AsSpan()) &&
            Exclude.AsSpan().SequenceEqual(other.Exclude.AsSpan()) &&
            DisableFilenameBasedTypeAcquisition == other.DisableFilenameBasedTypeAcquisition;
    }
}
