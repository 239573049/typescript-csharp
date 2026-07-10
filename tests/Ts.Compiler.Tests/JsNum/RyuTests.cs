// Ported from tsgo internal/jsnum/ryu_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

// Copyright 2018 Ulf Adams
//
// The contents of this file may be used under the terms of the Apache License,
// Version 2.0.
//
//    (See accompanying file LICENSE-Apache or copy at
//     http://www.apache.org/licenses/LICENSE-2.0)
//
// Alternatively, the contents of this file may be used under the terms of
// the Boost Software License, Version 1.0.
//    (See accompanying file LICENSE-Boost or copy at
//     https://www.boost.org/LICENSE_1_0.txt)
//
// Unless required by applicable law or agreed to in writing, this software
// is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.

// Copied from https://github.com/ulfjack/ryu/blob/1264a946ba66eab320e927bfd2362e0c8580c42f/ryu/tests/d2s_test.cc
// Modified to fit Number::toString's output.
//
// PORT-NOTE: in Go these vectors pin the jsnum-internal Ryū shortest-digit
// formatter; here they pin the equivalent contract of the BCL shortest
// round-trip digits + the ES6 layout layer in NumberString.cs (R05).

using System;
using Ts.JsNum;

namespace Ts.JsNum.Tests;

public static class RyuTests
{
    // Go: ieeeParts2Double
    internal static Number IeeeParts2Double(bool sign, uint ieeeExponent, ulong ieeeMantissa)
    {
        if (ieeeExponent > 2047)
        {
            throw new InvalidOperationException("ieeeExponent > 2047");
        }
        if (ieeeMantissa > MaxMantissa)
        {
            throw new InvalidOperationException("ieeeMantissa > maxMantissa");
        }
        ulong signBit = sign ? 1UL : 0UL;
        return BitConverter.UInt64BitsToDouble((signBit << 63) | ((ulong)ieeeExponent << 52) | ieeeMantissa);
    }

    internal const ulong MaxMantissa = (1UL << 53) - 1;

    // Go: ryuTests
    internal static readonly StringTest[] Tests =
    [
        new(2.2250738585072014e-308, "2.2250738585072014e-308"),
        new(JsNumTests.NumberFromBits(0x7fefffffffffffff), "1.7976931348623157e+308"),
        new(JsNumTests.NumberFromBits(1), "5e-324"),
        new(2.98023223876953125e-8, "2.9802322387695312e-8"),
        new(-2.109808898695963e16, "-21098088986959630"),
        new(4.940656e-318, "4.940656e-318"),
        new(1.18575755e-316, "1.18575755e-316"),
        new(2.989102097996e-312, "2.989102097996e-312"),
        new(9.0608011534336e15, "9060801153433600"),
        new(4.708356024711512e18, "4708356024711512000"),
        new(9.409340012568248e18, "9409340012568248000"),
        new(1.2345678, "1.2345678"),
        new(JsNumTests.NumberFromBits(0x4830F0CF064DD592), "5.764607523034235e+39"),
        new(JsNumTests.NumberFromBits(0x4840F0CF064DD592), "1.152921504606847e+40"),
        new(JsNumTests.NumberFromBits(0x4850F0CF064DD592), "2.305843009213694e+40"),
        new(1.2, "1.2"),
        new(1.23, "1.23"),
        new(1.234, "1.234"),
        new(1.2345, "1.2345"),
        new(1.23456, "1.23456"),
        new(1.234567, "1.234567"),
        new(1.2345678, "1.2345678"),
        new(1.23456789, "1.23456789"),
        new(1.234567895, "1.234567895"),
        new(1.2345678901, "1.2345678901"),
        new(1.23456789012, "1.23456789012"),
        new(1.234567890123, "1.234567890123"),
        new(1.2345678901234, "1.2345678901234"),
        new(1.23456789012345, "1.23456789012345"),
        new(1.234567890123456, "1.234567890123456"),
        new(1.2345678901234567, "1.2345678901234567"),
        new(4.294967294, "4.294967294"),
        new(4.294967295, "4.294967295"),
        new(4.294967296, "4.294967296"),
        new(4.294967297, "4.294967297"),
        new(4.294967298, "4.294967298"),
        new(IeeeParts2Double(false, 4, 0), "1.7800590868057611e-307"),
        new(IeeeParts2Double(false, 6, MaxMantissa), "2.8480945388892175e-306"),
        new(IeeeParts2Double(false, 41, 0), "2.446494580089078e-296"),
        new(IeeeParts2Double(false, 40, MaxMantissa), "4.8929891601781557e-296"),
        new(IeeeParts2Double(false, 1077, 0), "18014398509481984"),
        new(IeeeParts2Double(false, 1076, MaxMantissa), "36028797018963964"),
        new(IeeeParts2Double(false, 307, 0), "2.900835519859558e-216"),
        new(IeeeParts2Double(false, 306, MaxMantissa), "5.801671039719115e-216"),
        new(IeeeParts2Double(false, 934, 0x000FA7161A4D6E0C), "3.196104012172126e-27"),
        new(9007199254740991.0, "9007199254740991"),
        new(9007199254740992.0, "9007199254740992"),
        new(1.0e+0, "1"),
        new(1.2e+1, "12"),
        new(1.23e+2, "123"),
        new(1.234e+3, "1234"),
        new(1.2345e+4, "12345"),
        new(1.23456e+5, "123456"),
        new(1.234567e+6, "1234567"),
        new(1.2345678e+7, "12345678"),
        new(1.23456789e+8, "123456789"),
        new(1.23456789e+9, "1234567890"),
        new(1.234567895e+9, "1234567895"),
        new(1.2345678901e+10, "12345678901"),
        new(1.23456789012e+11, "123456789012"),
        new(1.234567890123e+12, "1234567890123"),
        new(1.2345678901234e+13, "12345678901234"),
        new(1.23456789012345e+14, "123456789012345"),
        new(1.234567890123456e+15, "1234567890123456"),
        new(1.0e+0, "1"),
        new(1.0e+1, "10"),
        new(1.0e+2, "100"),
        new(1.0e+3, "1000"),
        new(1.0e+4, "10000"),
        new(1.0e+5, "100000"),
        new(1.0e+6, "1000000"),
        new(1.0e+7, "10000000"),
        new(1.0e+8, "100000000"),
        new(1.0e+9, "1000000000"),
        new(1.0e+10, "10000000000"),
        new(1.0e+11, "100000000000"),
        new(1.0e+12, "1000000000000"),
        new(1.0e+13, "10000000000000"),
        new(1.0e+14, "100000000000000"),
        new(1.0e+15, "1000000000000000"),
        new(1000000000000001, "1000000000000001"),
        new(1000000000000010, "1000000000000010"),
        new(1000000000000100, "1000000000000100"),
        new(1000000000001000, "1000000000001000"),
        new(1000000000010000, "1000000000010000"),
        new(1000000000100000, "1000000000100000"),
        new(1000000001000000, "1000000001000000"),
        new(1000000010000000, "1000000010000000"),
        new(1000000100000000, "1000000100000000"),
        new(1000001000000000, "1000001000000000"),
        new(1000010000000000, "1000010000000000"),
        new(1000100000000000, "1000100000000000"),
        new(1001000000000000, "1001000000000000"),
        new(1010000000000000, "1010000000000000"),
        new(1100000000000000, "1100000000000000"),
        new(8.0, "8"),
        new(64.0, "64"),
        new(512.0, "512"),
        new(8192.0, "8192"),
        new(65536.0, "65536"),
        new(524288.0, "524288"),
        new(8388608.0, "8388608"),
        new(67108864.0, "67108864"),
        new(536870912.0, "536870912"),
        new(8589934592.0, "8589934592"),
        new(68719476736.0, "68719476736"),
        new(549755813888.0, "549755813888"),
        new(8796093022208.0, "8796093022208"),
        new(70368744177664.0, "70368744177664"),
        new(562949953421312.0, "562949953421312"),
        new(9007199254740992.0, "9007199254740992"),
        new(8.0e+3, "8000"),
        new(64.0e+3, "64000"),
        new(512.0e+3, "512000"),
        new(8192.0e+3, "8192000"),
        new(65536.0e+3, "65536000"),
        new(524288.0e+3, "524288000"),
        new(8388608.0e+3, "8388608000"),
        new(67108864.0e+3, "67108864000"),
        new(536870912.0e+3, "536870912000"),
        new(8589934592.0e+3, "8589934592000"),
        new(68719476736.0e+3, "68719476736000"),
        new(549755813888.0e+3, "549755813888000"),
        new(8796093022208.0e+3, "8796093022208000"),
    ];
}
