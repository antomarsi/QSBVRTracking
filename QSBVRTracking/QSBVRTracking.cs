using OWML.Common;
using OWML.ModHelper;

namespace QSBVRTracking;

public class QSBVRTracking : ModBehaviour
{
	private const string QSBUniqueName = "Raicuparta.QuantumSpaceBuddies";

	public static QSBVRTracking Instance;

	private VRPoseNetwork _network;

	public void Awake()
	{
		Instance = this;
	}

	public void Start()
	{
		ModHelper.Console.WriteLine($"My mod {nameof(QSBVRTracking)} is loaded!", MessageType.Success);

		// QSB is deliberately not a manifest dependency: declaring it would make QSB kick players without this addon.
		var qsb = ModHelper.Interaction.TryGetModApi<IQSBAPI>(QSBUniqueName);
		if (qsb == null)
		{
			ModHelper.Console.WriteLine("Quantum Space Buddies not found. VR tracking sync is inactive.", MessageType.Warning);
		}
		else
		{
			_network = new VRPoseNetwork(qsb, ModHelper);
			ModHelper.Console.WriteLine("QSB API found.", MessageType.Success);
		}

		OnCompleteSceneLoad(OWScene.TitleScreen, OWScene.TitleScreen); // We start on title screen
		LoadManager.OnCompleteSceneLoad += OnCompleteSceneLoad;
	}

	public override void Configure(IModConfig config)
	{
		TrackingConfig.Apply(config);
	}

	public void Update()
	{
		_network?.Update();
	}

	public void OnCompleteSceneLoad(OWScene previousScene, OWScene newScene)
	{
		_network?.OnSceneChanged();
		if (newScene != OWScene.SolarSystem) return;
		ModHelper.Console.WriteLine("Loaded into solar system!", MessageType.Success);
	}
}
