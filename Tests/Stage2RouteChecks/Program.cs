// Source-anchored S01-S06 routing decision test; no QCS runtime/DB/PDF access.
// Routing expression transcribed from MsdsSimilarDocumentLearning.ApplyIngredientAsync.
using System;
using System.Collections.Generic;

static class Program
{
    static string LegacyRoute(bool sameSupplier, bool sameProduct, bool sameFormat)
        => sameSupplier
            ? sameProduct ? (sameFormat ? "S01" : "S02") : (sameFormat ? "S03" : "S04")
            : sameFormat ? "S05" : "S06";

    static string ExpectedRoute(bool sameSupplier, bool sameProduct, bool sameFormat)
    {
        if (!sameSupplier) return sameFormat ? "S05" : "S06";
        if (sameProduct) return sameFormat ? "S01" : "S02";
        return sameFormat ? "S03" : "S04";
    }

    static int Main()
    {
        int count=0;
        foreach (bool supplier in new[] { false, true })
        foreach (bool product in new[] { false, true })
        foreach (bool format in new[] { false, true })
        {
            string actual=LegacyRoute(supplier,product,format);
            string expected=ExpectedRoute(supplier,product,format);
            if(actual!=expected) { Console.Error.WriteLine($"FAIL: {supplier}/{product}/{format}: {actual} != {expected}"); return 1; }
            Console.WriteLine($"PASS route: supplier={supplier}, product={product}, format={format} => {actual}");
            count++;
        }
        Console.WriteLine($"PASS: S01-S06 route decision table ({count}/{count}); no production DB writes");
        Console.WriteLine("NOT TESTED: original QCS runtime execution, candidate eligibility, PDF evidence, S00 same-document branch, S03 Section11 branch, new-engine end-to-end matching");
        return 0;
    }
}
