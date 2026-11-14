// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;

namespace Parti.Core;

public static class SimpleId
{
    public static Dictionary<Guid, string> Assign(IEnumerable<(Guid instanceGuid, string name, string? nickname)> components)
    {
        var counters = new Dictionary<string, int>();
        var result = new Dictionary<Guid, string>();
        foreach (var (guid, name, nick) in components)
        {
            var baseId = MakeBase(name, nick);
            counters.TryGetValue(baseId, out var n);
            n++;
            counters[baseId] = n;
            result[guid] = $"{baseId}{n}";
        }
        return result;
    }

    private static string MakeBase(string name, string? nickname)
    {
        if (!string.IsNullOrWhiteSpace(nickname)
            && !string.Equals(nickname.Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            var clean = new string(nickname.ToLowerInvariant()
                .Where(char.IsLetterOrDigit).Take(10).ToArray());
            if (clean.Length > 0) return clean;
        }
        var firstWord = new string(name.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        var nameClean = firstWord.Length > 3 ? firstWord.Substring(0, 3) : firstWord;
        return nameClean.Length > 0 ? nameClean : "node";
    }
}
