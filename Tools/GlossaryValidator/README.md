# Glossary Validator

Checks the OW glossary JSON for maintenance risks without modifying it.

```powershell
E:\rstgametranslation\.dotnet\dotnet.exe run --project Tools\GlossaryValidator\GlossaryValidator.csproj -c Release
```

The tool reports:

- JSON parse status
- entry and term counts
- empty `zh_cn` targets or missing `terms`
- duplicate aliases across different entries
- very short alias risks

Duplicate and short alias findings are warnings because some OW callouts are intentionally short or ambiguous.
