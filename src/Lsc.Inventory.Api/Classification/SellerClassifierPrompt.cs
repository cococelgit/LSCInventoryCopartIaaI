namespace Lsc.Inventory.Api.Classification;

public static class SellerClassifierPrompt
{
    public const string Version = "seller_classifier_v1";

    public const string System = """
You classify automotive auction seller names for La Subasta Cubana.

Your job is classification, not guessing. Use only the seller name and explicit context provided. Do not invent a company identity, ownership, industry, website, evidence, or source. If the name is ambiguous or the available information is insufficient, return category \"unknown\".

Allowed categories only: insurance, dealer, finance, rental_fleet, government, repossession_bank, other, unknown.

Definitions:
- insurance: insurance companies, insurers, casualty or claims entities, salvage divisions operated by insurers.
- dealer: franchised dealers, independent dealers, auto groups, motor groups.
- finance: finance companies, leasing companies, auto-credit companies.
- rental_fleet: rental companies or commercial vehicle fleets.
- government: federal, state, county, city, municipal or public agencies.
- repossession_bank: banks, credit unions, lenders, repossession operators.
- other: a seller identity is clear but does not fit the categories above.
- unknown: the name is missing, ambiguous, generic, truncated, or not enough evidence exists to classify it safely.

Rules:
1. Never infer a category from a random word without sufficient context.
2. Do not confuse a vehicle location, auction facility, or auction platform with the seller.
3. Do not classify Copart or IAAI as the seller unless the input explicitly identifies them as the vehicle owner.
4. Confidence must reflect evidence. Use low confidence for ambiguous names.
5. If uncertain, return unknown rather than other.
6. Return JSON only, exactly matching the requested schema.
""";

    public const string UserTemplate = """
Classify this seller name.

seller_name_raw: {0}
seller_name_normalized: {1}
platform: {2}
provider_seller_type: {3}
provider_seller_class: {4}

No external evidence was supplied. Use only the information above.
""";

    public static object ResponseFormat => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "seller_classification",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    category = new { type = "string", @enum = new[] { "insurance", "dealer", "finance", "rental_fleet", "government", "repossession_bank", "other", "unknown" } },
                    confidence = new { type = "number", minimum = 0, maximum = 1 },
                    needs_review = new { type = "boolean" },
                    reason = new { type = "string" },
                    evidence_type = new { type = "string", @enum = new[] { "name_only", "insufficient_evidence" } }
                },
                required = new[] { "category", "confidence", "needs_review", "reason", "evidence_type" },
                additionalProperties = false
            }
        }
    };
}
