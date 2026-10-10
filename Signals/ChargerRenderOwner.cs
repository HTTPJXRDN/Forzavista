// Independently mapped Steam 6.461.691.0 and Xbox 3.461.691.0. These are in-object links, established from the
// player render proxy and CarScene allocation, not neighboring allocations.
internal sealed class ChargerRenderOwner
{
    private readonly Func<ulong, int, byte[]> read;
    private readonly Func<bool> sameCar;
    private readonly ulong module, vehicle, component;
    private readonly ForzavistaFreeRoam.DrlBuildMapping build;
    private readonly ulong proxy, model, scene, sceneControl, poseControl;
    internal ulong Pose { get; }

    private static bool Heap(ulong p) => p is >= 0x10000000 and < 0x7FF000000000 && (p & 7) == 0;
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Charger current-player render scene / lamp pose changed; no writes.");
    }
    private ulong Pointer(ulong address)
    {
        var bytes = read(address, 8);
        Require(bytes.Length == 8);
        return BitConverter.ToUInt64(bytes);
    }
    private ChargerRenderOwner(Func<ulong, int, byte[]> read, Func<bool> sameCar,
        ulong module, ulong vehicle, ulong component, ForzavistaFreeRoam.DrlBuildMapping build)
    {
        this.read = read; this.sameCar = sameCar;
        this.module = module; this.vehicle = vehicle; this.component = component; this.build = build;
        Require(module >= 0x10000 && module < ulong.MaxValue - build.SizeOfImage && Heap(vehicle) && Heap(component) && sameCar());
        proxy = Pointer(vehicle + 0x7918);
        Require(Heap(proxy) && Pointer(component + 0xB8) == proxy && Pointer(proxy) == module + build.RenderProxyVtableRva);
        model = Pointer(proxy + 8);
        Require(Heap(model) && Pointer(model) == module + build.PlayerRenderModelVtableRva);
        scene = Pointer(model + 0x7B0); sceneControl = Pointer(model + 0x7B8);
        Require(Heap(scene) && Heap(sceneControl) && Pointer(scene) == module + build.CarSceneVtableRva &&
            Pointer(sceneControl) == module + build.SceneControlVtableRva && Pointer(sceneControl + 0x10) == scene);
        Pose = Pointer(scene + 0x438); poseControl = Pointer(scene + 0x440);
        Require(Heap(Pose) && poseControl == Pose - 0x10 && Pointer(Pose) == module + build.PoseVtableRva &&
            Pointer(poseControl) == module + build.PoseControlVtableRva);
        Validate(); // Reject a root swap during capture, even within the same car.
    }
    internal static ChargerRenderOwner Capture(Func<ulong, int, byte[]> read, Func<bool> sameCar,
        ulong module, ulong vehicle, ulong component, ForzavistaFreeRoam.DrlBuildMapping? build = null) =>
        new(read, sameCar, module, vehicle, component, build ?? ForzavistaFreeRoam.DrlBuildMapping.Steam);

    internal void Validate()
    {
        Require(sameCar() && Pointer(vehicle + 0x7918) == proxy && Pointer(component + 0xB8) == proxy &&
            Pointer(proxy) == module + build.RenderProxyVtableRva && Pointer(proxy + 8) == model &&
            Pointer(model) == module + build.PlayerRenderModelVtableRva && Pointer(model + 0x7B0) == scene &&
            Pointer(model + 0x7B8) == sceneControl && Pointer(scene) == module + build.CarSceneVtableRva &&
            Pointer(sceneControl) == module + build.SceneControlVtableRva && Pointer(sceneControl + 0x10) == scene &&
            Pointer(scene + 0x438) == Pose && Pointer(scene + 0x440) == poseControl &&
            Pointer(Pose) == module + build.PoseVtableRva && Pointer(poseControl) == module + build.PoseControlVtableRva && sameCar());
    }
    // Scene retirement is expected when the same car moves into/out of the
    // garage. Follow only links still equal to our captured owner. A failed
    // query stops here instead of visiting old lamp/material allocations.
    // This is a read-only lifetime check; Validate still guards every write.
    internal bool IsCurrentScene(Func<ulong, ulong?> pointer) =>
        pointer(vehicle + 0x7918) == proxy && pointer(component + 0xB8) == proxy &&
        pointer(proxy) == module + build.RenderProxyVtableRva && pointer(proxy + 8) == model &&
        pointer(model) == module + build.PlayerRenderModelVtableRva && pointer(model + 0x7B0) == scene &&
        pointer(model + 0x7B8) == sceneControl && pointer(scene) == module + build.CarSceneVtableRva &&
        pointer(sceneControl) == module + build.SceneControlVtableRva && pointer(sceneControl + 0x10) == scene &&
        pointer(scene + 0x438) == Pose && pointer(scene + 0x440) == poseControl &&
        pointer(Pose) == module + build.PoseVtableRva && pointer(poseControl) == module + build.PoseControlVtableRva;
    internal bool MatchesInstance(ulong instance) => Heap(instance) && Pointer(instance + 0x38) == Pose;

    internal static void SelfTest()
    {
        const ulong module = 0x7FF700000000, vehicle = 0x20000000, component = 0x21000000,
            proxy = 0x22000000, model = 0x23000000, scene = 0x24000000, control = 0x25000000,
            pose = 0x26000000, instance = 0x27000000;
        var memory = new Dictionary<ulong, ulong>
        {
            [vehicle + 0x7918] = proxy, [component + 0xB8] = proxy,
            [proxy] = module + 0x6EBD320, [proxy + 8] = model,
            [model] = module + 0x6FA60C0, [model + 0x7B0] = scene, [model + 0x7B8] = control,
            [scene] = module + 0x65B4398, [control] = module + 0x6FA3330, [control + 0x10] = scene,
            [scene + 0x438] = pose, [scene + 0x440] = pose - 0x10,
            [pose] = module + 0x6474B18, [pose - 0x10] = module + 0x6475348
        };
        var same = true; var shortRead = false; var reads = new HashSet<ulong>(); var count = 0;
        byte[] Read(ulong p, int n)
        {
            if (n != 8 || !memory.ContainsKey(p)) throw new InvalidOperationException("Unexpected fake-memory read.");
            reads.Add(p);
            return shortRead ? new byte[7] : BitConverter.GetBytes(memory[p]);
        }
        void Check(bool value) { if (!value) throw new InvalidOperationException($"Rooted Charger check {count + 1} failed."); count++; }
        void Reject(Action action)
        {
            var rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
        }
        var owner = Capture(Read, () => same, module, vehicle, component);
        owner.Validate(); Check(owner.Pose == pose);
        var roots = memory.Keys.ToArray();
        Check(reads.SetEquals(roots)); // No old proxy+18 / bindings+90 / pose-context+50 reads.
        var queries = new HashSet<ulong>();
        ulong? Query(ulong address)
        {
            queries.Add(address);
            return memory.TryGetValue(address, out var value) ? value : null;
        }
        Check(owner.IsCurrentScene(Query));
        foreach (var address in roots)
        {
            var before = memory[address]; memory[address] = 0;
            Reject(owner.Validate);
            Reject(() => Capture(Read, () => same, module, vehicle, component));
            Check(!owner.IsCurrentScene(Query));
            memory[address] = before;
            memory.Remove(address);
            Check(!owner.IsCurrentScene(Query)); // Freed owner queries are normal retirement, without exceptions.
            memory[address] = before;
        }
        queries.Clear(); memory[vehicle + 0x7918] = proxy + 0x1000;
        Check(!owner.IsCurrentScene(Query) && queries.SetEquals([vehicle + 0x7918]));
        memory[vehicle + 0x7918] = proxy;
        queries.Clear(); memory[model + 0x7B0] = scene + 0x1000;
        Check(!owner.IsCurrentScene(Query) && !queries.Contains(scene) && !queries.Contains(pose));
        memory[model + 0x7B0] = scene;
        memory[instance + 0x38] = pose;
        Check(owner.MatchesInstance(instance));
        memory[instance + 0x38] = pose + 0x1000;
        Check(!owner.MatchesInstance(instance));
        Check(!owner.MatchesInstance(0));
        same = false; Reject(owner.Validate); Reject(() => Capture(Read, () => same, module, vehicle, component)); same = true;
        shortRead = true; Reject(owner.Validate); shortRead = false;
        // A valid-looking but different scene/pose must not permit stale use.
        memory[model + 0x7B0] = scene + 0x1000; Reject(owner.Validate); memory[model + 0x7B0] = scene;
        memory[scene + 0x438] = pose + 0x1000; Reject(owner.Validate); memory[scene + 0x438] = pose;
        owner.Validate(); Check(true);
        Console.WriteLine($"PASS: {count} pure rooted Charger ownership checks; no game process opened or written.");
    }
}
