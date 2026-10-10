namespace ForzavistaFreeRoam;

// Exact supported executable identities. Xbox fields were mapped independently
// by COL/hierarchy/method shape plus unique normalized code, then read-only
// current-player checks. No relative platform-wide address adjustment is used.
internal sealed record DrlBuildMapping
{
    internal required string BuildName { get; init; }
    internal required uint PeTimestamp { get; init; }
    internal required uint SizeOfImage { get; init; }
    internal required ulong RenderProxyVtableRva { get; init; }
    internal required ulong PlayerRenderModelVtableRva { get; init; }
    internal required ulong CarSceneVtableRva { get; init; }
    internal required ulong SceneControlVtableRva { get; init; }
    internal required ulong PoseVtableRva { get; init; }
    internal required ulong PoseControlVtableRva { get; init; }
    internal required ulong CarRenderModelWrapperVtableRva { get; init; }
    internal required ulong CarModelInstanceVtableRva { get; init; }
    internal required ulong CarModelResourceVtableRva { get; init; }
    internal required ulong MaterialVtableRva { get; init; }
    internal required ulong MaterialControlVtableRva { get; init; }
    internal required ulong ColorParameterVtableRva { get; init; }
    internal required ulong FloatParameterVtableRva { get; init; }
    internal required ulong BoolParameterVtableRva { get; init; }
    internal required ulong VectorParameterVtableRva { get; init; }
    internal required ulong TextureParameterVtableRva { get; init; }
    internal required ulong TriggerParameterVtableRva { get; init; }
    internal required ulong TextureResourceVtableRva { get; init; }
    internal required ulong ConstantBufferVtableRva { get; init; }
    internal required ulong LightEvaluatorRva { get; init; }
    internal required ulong LightVehicleVtableRva { get; init; }
    internal required ulong UploadRva { get; init; }
    internal required ulong WalkRva { get; init; }
    internal required ulong AllocatorGlobalRva { get; init; }
    internal required IReadOnlyList<(long Offset, string Hex)> FadeSignatures { get; init; }

    internal static DrlBuildMapping Steam { get; } = new()
    {
        BuildName = "Steam 6.461.691.0", PeTimestamp = 0x6ABA8552, SizeOfImage = 0xB3C0000,
        RenderProxyVtableRva = 0x6EBD320,
        PlayerRenderModelVtableRva = 0x6FA60C0,
        CarSceneVtableRva = 0x65B4398,
        SceneControlVtableRva = 0x6FA3330,
        PoseVtableRva = 0x6474B18,
        PoseControlVtableRva = 0x6475348,
        CarRenderModelWrapperVtableRva = 0x65C6408,
        CarModelInstanceVtableRva = 0x6474E38,
        CarModelResourceVtableRva = 0x6474CA8,
        MaterialVtableRva = 0x65EB720,
        MaterialControlVtableRva = 0x6475280,
        ColorParameterVtableRva = 0x6578438,
        FloatParameterVtableRva = 0x6578478,
        BoolParameterVtableRva = 0x65784B8,
        VectorParameterVtableRva = 0x65785B8,
        TextureParameterVtableRva = 0x6578638,
        TriggerParameterVtableRva = 0x65786F8,
        TextureResourceVtableRva = 0x6577DD0,
        ConstantBufferVtableRva = 0x65E6788,
        LightEvaluatorRva = 0x2AAA2E0,
        LightVehicleVtableRva = 0x64A3D38,
        UploadRva = 0x14FEC00,
        WalkRva = 0x14FF170,
        AllocatorGlobalRva = 0xA82C398,
        FadeSignatures = [
            (576, "E88BFBFFFF"),
            (-169, "E8D4FAFFFF"),
            (-1488, "48895C2408574883EC208B01488BFAFFC8488BD983F814"),
            (-1436, "4863430848634B048B048289048A"),
            (-1135, "48634308F30F100482F30F59431448634304F30F110482"),
            (-652, "449DAA02"),
            (-620, "719EAA02"),
        ]
    };

    internal static DrlBuildMapping Xbox { get; } = new()
    {
        BuildName = "Microsoft Store 3.461.691.0", PeTimestamp = 0x6ABA856E, SizeOfImage = 0xB3AD000,
        RenderProxyVtableRva = 0x6EAB7F0,
        PlayerRenderModelVtableRva = 0x6F94730,
        CarSceneVtableRva = 0x65A15B0,
        SceneControlVtableRva = 0x6F91930,
        PoseVtableRva = 0x6461A58,
        PoseControlVtableRva = 0x6462288,
        CarRenderModelWrapperVtableRva = 0x65B3608,
        CarModelInstanceVtableRva = 0x6461D78,
        CarModelResourceVtableRva = 0x6461BE8,
        MaterialVtableRva = 0x65D8900,
        MaterialControlVtableRva = 0x64621C0,
        ColorParameterVtableRva = 0x65655F8,
        FloatParameterVtableRva = 0x6565638,
        BoolParameterVtableRva = 0x6565678,
        VectorParameterVtableRva = 0x6565778,
        TextureParameterVtableRva = 0x65657F8,
        TriggerParameterVtableRva = 0x65658B8,
        TextureResourceVtableRva = 0x6564F90,
        ConstantBufferVtableRva = 0x65D3960,
        LightEvaluatorRva = 0x2A2BD70,
        LightVehicleVtableRva = 0x6490C18,
        UploadRva = 0x1487010,
        WalkRva = 0x1487580,
        AllocatorGlobalRva = 0xA8256C0,
        FadeSignatures = [
            (576, "E88BFBFFFF"),
            (-169, "E8D4FAFFFF"),
            (-1488, "48895C2408574883EC208B01488BFAFFC8488BD983F814"),
            (-1436, "4863430848634B048B048289048A"),
            (-1135, "48634308F30F100482F30F59431448634304F30F110482"),
            (-652, "D4B7A202"),
            (-620, "01B9A202"),
        ]
    };

    internal static DrlBuildMapping? ForProfile(GameBuildProfile profile) =>
        new[] { Steam, Xbox }.FirstOrDefault(mapping =>
            mapping.BuildName == profile.Name && mapping.PeTimestamp == profile.PeTimestamp &&
            mapping.SizeOfImage == profile.SizeOfImage &&
            mapping.LightEvaluatorRva == profile.LightEvaluatorRva &&
            mapping.LightVehicleVtableRva == profile.LightVehicleVtableRva);
}
