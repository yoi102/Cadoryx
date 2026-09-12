namespace Cadoryx.Db;

/// <summary>Unknown optional sections are preserved byte-for-byte; editing is disabled until supported.</summary>
public sealed record PreservedSection(string Kind,int SchemaVersion,AssetId PayloadAssetId,string Encoding="json");
