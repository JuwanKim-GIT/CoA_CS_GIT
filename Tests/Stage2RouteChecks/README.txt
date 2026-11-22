QCS Stage 2 — S2-06 route decision compatibility checks

Purpose: verify the 8 combinations of supplier/product/format against the exact S01-S06 subtype decision expression in the uploaded QCS source (MsdsSimilarDocumentLearning.cs, lines 185-189).

Run: open Tests/Stage2RouteChecks/Stage2RouteChecks.csproj in Visual Studio, Ctrl+F5.

This is a standalone routing-decision test. It does not execute the original QCS method or new engine, does not verify PDFs, DB candidates or learning outcomes, and cannot close Stage 2 Gate by itself.

S03 is overloaded in current source: Section11 corrosivity ApplyAsync and ingredient route ApplyIngredientAsync. Both must be covered independently before final compatibility signoff.
