using DV.Interaction;
using DV.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Editor.Components.Player;
using Multiplayer.Networking.Data.Player;
using System.Collections.Generic;
using UnityChan;
using UnityEngine;

namespace Multiplayer.Components.Networking.Player;

/// <summary>
/// Represents a networked player in the multiplayer environment, handling movement, item holding, and visual state
/// </summary>
public class NetworkedPlayer : MonoBehaviour
{
    #region Static Setup
    private static Vector3 itemAnchorOffset = new(0.2f, 1.5f, 0.4f);

    /// <summary>
    /// Captures the standard offset position for held items relative to the player transform
    /// for mapping to a NetworkedPlayer.
    /// This must be called as soon as the world is loaded, before the local player moves or crouches.
    /// </summary>
    public static void CaptureItemAnchorOffset()
    {
        //todo: there's some minor inconsistency with return values and may be related to:
        // - the direction/rotation of the camera
        // - player loading status (maybe posistion hasn't settled yet)
        if (!VRManager.IsVREnabled())
        {
            itemAnchorOffset = PlayerManager.PlayerTransform.InverseTransformPoint(ItemPositionController.Instance.itemAnchor.position);
            Multiplayer.LogDebug(() => $"NetworkedPlayer.CaptureItemAnchorOffset() itemAnchorOffset: {itemAnchorOffset}");
        }
    }
    #endregion

    private const float LERP_SPEED = 5.0f;
    private const float MAX_LEAN_ANGLE = 50f;
    private const float LEAN_SMOOTHING_DURATION = 0.1f;
    private const float HEAD_LEAN_MULTIPLIER = 1.5f;

    public byte PlayerId { get; set; }
    public string CrewName { get; set; }
    public bool IsVR { get; set; }

    private GameObject playerModel;
    private AnimationHandler animationHandler;
    private NameTag nameTag;
    private int ping;
    private NetworkedPlayerIKHandler ikHandler;

    private string username;

    public string Username
    {
        get => username;
        set
        {
            username = value;
            nameTag?.SetUsername(value);
        }
    }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(CrewName))
                return username;
            return $"[{CrewName}] {username}";
        }
    }

    // World Positioning
    internal bool IsOnCar { get; private set; }
    internal NetworkedTrainCar OccupiedCar { get; private set; }

    private Transform selfTransform;
    private PlayerPostureFlags currentPosture;

    // Head tracking
    private Transform headTransform;
    private Quaternion headBaseWorldRotation = Quaternion.identity;
    private float currentHeadPitch;
    private float targetHeadPitch;

    // Spine tracking
    private Transform spineTransform;
    private Quaternion spineBaseWorldRotation = Quaternion.identity;

    // Player movement and rotation
    private Vector3 targetPos;
    private Quaternion targetRotation;
    private Vector2 moveDir;
    private Vector2 targetMoveDir;

    private float currentLeanAngle;
    private float angleSmoothRefVel;
    private float currentSitHeight;

    // VR hand tracking — targets set from incoming packets
    private Transform leftHandTransform;
    private Transform rightHandTransform;
    private Vector3 targetLeftHandPos;
    private Quaternion targetLeftHandRot = Quaternion.identity;
    private Vector3 targetRightHandPos;
    private Quaternion targetRightHandRot = Quaternion.identity;

    // Current lerped values — tracked independently to avoid Animator fighting
    private Vector3 currentLeftHandWorldPos;
    private Quaternion currentLeftHandWorldRot = Quaternion.identity;
    private Vector3 currentRightHandWorldPos;
    private Quaternion currentRightHandWorldRot = Quaternion.identity;
    private bool handTrackingInitialized;

    // Inventory and item holding
    private GameObject inventoryRoot;
    public GameObject RightHandItemGO { get; private set; }
    private readonly Dictionary<Collider, bool> disabledRHItemColliders = [];
    public GameObject LeftHandItemGO { get; private set; }
    private readonly Dictionary<Collider, bool> disabledLHItemColliders = [];
    private bool rightInteractionAllowed, leftInteractionAllowed;
    private Vector3? itemHoldPos;
    private Quaternion? itemHoldRot;
    private Vector3? leftItemHoldPos;
    private Quaternion? leftItemHoldRot;

    WindPhysicsController windController;

    private bool isCulled;
    public bool IsCulled
    {
        get => isCulled;
        set
        {
            if (isCulled == value)
                return;

            isCulled = value;

            playerModel?.SetActive(!value);
            nameTag?.gameObject.SetActive(!value);
        }
    }

    protected void Awake()
    {
        nameTag = GetComponentInChildren<NameTag>();

        nameTag.LookTarget = PlayerManager.ActiveCamera.transform;
        PlayerManager.CameraChanged += () => nameTag.LookTarget = PlayerManager.ActiveCamera.transform;

        if (name != null)
            nameTag.SetUsername(name);

        OnSettingsUpdated(Multiplayer.Settings);
        Settings.OnSettingsUpdated += OnSettingsUpdated;

        selfTransform = transform;
        targetPos = selfTransform.position;
        targetRotation = selfTransform.rotation;

        targetHeadPitch = 0f;

        moveDir = Vector2.zero;
        targetMoveDir = Vector2.zero;

        currentPosture = PlayerPostureFlags.None;

        var clampedSitHeight = Mathf.Clamp
            (
                CustomFirstPersonController.PLAYER_SITTING_HEIGHT,
                CustomFirstPersonController.MIN_PLAYER_SITTING_HEIGHT,
                CustomFirstPersonController.MAX_PLAYER_SITTING_HEIGHT
            );
        currentSitHeight = Mathf.InverseLerp
            (
                CustomFirstPersonController.MIN_PLAYER_SITTING_HEIGHT,
                CustomFirstPersonController.MAX_PLAYER_SITTING_HEIGHT,
                clampedSitHeight
            );
    }

    protected void OnDestroy()
    {
        Settings.OnSettingsUpdated -= OnSettingsUpdated;
    }

    private void OnSettingsUpdated(Settings settings)
    {
        nameTag.ShowUsername(settings.ShowNameTags);
        nameTag.ShowPing(settings.ShowNameTags && settings.ShowPingInNameTags);
    }

    public void ChangeModel(GameObject newModel)
    {
        if (newModel == playerModel || newModel == null)
            return;

        if (playerModel != null)
        {
            animationHandler = null;
            DestroyImmediate(playerModel);
            headTransform = null;
            leftHandTransform = null;
            rightHandTransform = null;
            handTrackingInitialized = false;
            windController = null;
        }

        playerModel = Instantiate(newModel, transform);
        animationHandler = playerModel.GetComponent<AnimationHandler>();

        // If the model is using wind physics, e.g. for hair, add the WindPhysicsController to manage effects on and off the car
        if (playerModel.GetComponentInChildren<SpringManager>(true) != null)
            windController = playerModel.AddComponent<WindPhysicsController>();

        var animator = playerModel.GetComponentInChildren<Animator>(true);
        if (animator != null)
        {
            if (IsVR)
            {
                // Track VR Networked player's IK state for hands and feet
                ikHandler = animator.gameObject.AddComponent<NetworkedPlayerIKHandler>();
                ikHandler.IsActive = false;
            }

            headTransform = animator.GetBoneTransform(HumanBodyBones.Head);
            if (headTransform == null)
                Multiplayer.LogWarning($"Head bone not found in model {newModel.name}. Head tracking will not work");

            spineTransform = animator.GetBoneTransform(HumanBodyBones.Spine);

            leftHandTransform = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            rightHandTransform = animator.GetBoneTransform(HumanBodyBones.RightHand);

            if (leftHandTransform == null || rightHandTransform == null)
                Multiplayer.LogWarning($"Hand bones not found in model {newModel.name}. VR hand tracking will not work");
        }
        else
        {
            Multiplayer.LogWarning($"Animator not found in model {newModel.name}. Tracking will not work");
        }

        if (spineTransform == null)
        {
            // Fall back to using the model's transform if the spine bone is not found
            spineTransform = playerModel.transform;
        }

        spineBaseWorldRotation = Quaternion.Inverse(selfTransform.rotation) * spineTransform.rotation;

        if (headTransform != null)
            headBaseWorldRotation = Quaternion.Inverse(selfTransform.rotation) * headTransform.rotation;

        SetPosture(currentPosture);

        if (IsCulled)
            playerModel.SetActive(false);
    }

    public void SetPing(int ping)
    {
        nameTag?.SetPing(ping);
        this.ping = ping;
    }

    public int GetPing()
    {
        return ping;
    }

    protected void Update()
    {
        if (IsCulled)
        {
            if (IsOnCar)
                selfTransform.localPosition = targetPos;
            else
                selfTransform.position = targetPos + WorldMover.currentMove;

            selfTransform.rotation = targetRotation;
            return;
        }

        float t = Time.deltaTime * LERP_SPEED;

        Vector3 position = Vector3.Lerp(
            IsOnCar ? selfTransform.localPosition : selfTransform.position,
            IsOnCar ? targetPos : targetPos + WorldMover.currentMove,
            t);

        // Calculate smoothed head pitch for use in VR and nonVR head positioning and nonVR item positioning
        currentHeadPitch = Mathf.Lerp(currentHeadPitch, targetHeadPitch, t);

        moveDir = Vector2.Lerp(moveDir, targetMoveDir, t);
        animationHandler?.SetMoveDir(moveDir);

        if (!IsVR)
            animationHandler?.SetSitHeight(currentSitHeight);

        if (IsOnCar && OccupiedCar != null)
        {
            selfTransform.localPosition = position;

            // Calculate a world-up-respecting rotation
            // This creates a rotation where Y points up in world space
            // but the forward direction aligns with the car's forward projected onto the horizontal plane
            Vector3 carForward = OccupiedCar.transform.forward;
            Vector3 worldUp = Vector3.up;

            // Project car's forward onto the horizontal plane
            Vector3 horizontalForward = Vector3.ProjectOnPlane(carForward, worldUp).normalized;
            if (horizontalForward.sqrMagnitude < 0.001f)
                horizontalForward = Vector3.ProjectOnPlane(OccupiedCar.transform.right, worldUp).normalized;

            // Create base orientation aligned with world up but facing car's forward direction
            Quaternion baseRotation = Quaternion.LookRotation(horizontalForward, worldUp);

            // Calculate the relative rotation: how much is the player rotated relative to the car?
            float carYaw = baseRotation.eulerAngles.y;
            float playerYaw = targetRotation.eulerAngles.y;
            float relativeYaw = playerYaw - carYaw;

            // Apply the desired Y rotation (player's facing direction) on top of this base rotation
            Quaternion targetWorldRotation = baseRotation * Quaternion.Euler(0, relativeYaw, 0);

            // Apply rotation in world space despite being a child transform
            selfTransform.rotation = Quaternion.Lerp(selfTransform.rotation, targetWorldRotation, t);
        }
        else
        {
            selfTransform.position = position;
            selfTransform.rotation = Quaternion.Lerp(transform.rotation, targetRotation, t);
        }


    }

    /// <summary>
    /// LateUpdate is called after animators have updated, allowing us to apply our own transformations on top of the animated posture.
    /// </summary>
    protected void LateUpdate()
    {
        if (IsCulled)
            return;

        if (!IsVR)
        {
            float targetLeanAngle = 0f;
            if (currentPosture.HasFlag(PlayerPostureFlags.LeanLeft))
                targetLeanAngle = MAX_LEAN_ANGLE;
            else if (currentPosture.HasFlag(PlayerPostureFlags.LeanRight))
                targetLeanAngle = -MAX_LEAN_ANGLE;

            currentLeanAngle = Mathf.SmoothDamp(currentLeanAngle, targetLeanAngle, ref angleSmoothRefVel, LEAN_SMOOTHING_DURATION);
        }

        ApplySpineAndHeadRotation();

        if (IsVR)
            ApplyHandTracking();
        UpdateHeldItem(true);
        UpdateHeldItem(false);
    }

    private void ApplySpineAndHeadRotation()
    {
        if (spineTransform != null)
        {
            // Reconstruct the base animated posture for this model in world space
            Quaternion currentModelSpineBase = selfTransform.rotation * spineBaseWorldRotation;

            // Define standard look/lean vectors using the main uniform player root
            // Side lean is always spinning around the root's global FORWARD axis
            Quaternion leanOffset = Quaternion.AngleAxis(currentLeanAngle, selfTransform.forward);

            // Directly assign the uniform world rotation 
            spineTransform.rotation = leanOffset * currentModelSpineBase;
        }

        if (headTransform == null)
            return;

        Quaternion currentModelHeadBase = selfTransform.rotation * headBaseWorldRotation;
        Quaternion pitchRotation = Quaternion.AngleAxis(currentHeadPitch, selfTransform.right);
        Quaternion leanTiltRotation = Quaternion.AngleAxis(currentLeanAngle * HEAD_LEAN_MULTIPLIER, selfTransform.forward);
        headTransform.rotation = pitchRotation * leanTiltRotation * currentModelHeadBase;
    }

    private void ApplyHandTracking()
    {
        if (!handTrackingInitialized || ikHandler == null)
            return;

        float t = Time.deltaTime * LERP_SPEED;

        currentLeftHandWorldPos = Vector3.Lerp(currentLeftHandWorldPos, targetLeftHandPos, t);
        currentLeftHandWorldRot = Quaternion.Lerp(currentLeftHandWorldRot, targetLeftHandRot, t);

        currentRightHandWorldPos = Vector3.Lerp(currentRightHandWorldPos, targetRightHandPos, t);
        currentRightHandWorldRot = Quaternion.Lerp(currentRightHandWorldRot, targetRightHandRot, t);

        ikHandler.LeftHandPosition = selfTransform.position + targetRotation * currentLeftHandWorldPos;
        ikHandler.LeftHandRotation = targetRotation * currentLeftHandWorldRot;
        ikHandler.RightHandPosition = selfTransform.position + targetRotation * currentRightHandWorldPos;
        ikHandler.RightHandRotation = targetRotation * currentRightHandWorldRot;
    }

    /// <summary>
    /// Feed networked tracking data into the NetworkedPlayer to update its position, rotation, and posture.
    /// </summary>
    /// <param name="trackingData"></param>
    /// <param name="posture"></param>
    /// <param name="movePacketIsOnCar"></param>
    public void UpdatePosition(PlayerTrackingData trackingData, PlayerPostureFlags posture, bool movePacketIsOnCar)
    {
        if (trackingData.Position.HasValue)
            targetPos = trackingData.Position.Value;

        if (trackingData.MoveDirection.HasValue)
        {
            targetMoveDir = trackingData.MoveDirection.Value;
        }

        if (trackingData.SitHeight.HasValue)
            currentSitHeight = Mathf.Clamp01(trackingData.SitHeight.Value);

        SetPosture(posture);

        if (IsOnCar != movePacketIsOnCar)
            return;

        if (trackingData.RotationY.HasValue)
            targetRotation = Quaternion.Euler(0, trackingData.RotationY.Value, 0);

        if (trackingData.LookPosition.HasValue)
            targetHeadPitch = trackingData.LookPosition.Value;

        if (trackingData.LeftHandPosition.HasValue)
            targetLeftHandPos = trackingData.LeftHandPosition.Value;
        if (trackingData.LeftHandRotation.HasValue)
            targetLeftHandRot = trackingData.LeftHandRotation.Value;
        if (trackingData.RightHandPosition.HasValue)
            targetRightHandPos = trackingData.RightHandPosition.Value;
        if (trackingData.RightHandRotation.HasValue)
            targetRightHandRot = trackingData.RightHandRotation.Value;

        // Todo: improve sync, the arms can be a little spaghetti-y
        if (!handTrackingInitialized && trackingData.LeftHandPosition.HasValue &&
            trackingData.LeftHandRotation.HasValue && trackingData.RightHandPosition.HasValue &&
            trackingData.RightHandRotation.HasValue)
        {
            currentLeftHandWorldPos = targetLeftHandPos;
            currentLeftHandWorldRot = targetLeftHandRot;
            currentRightHandWorldPos = targetRightHandPos;
            currentRightHandWorldRot = targetRightHandRot;
            handTrackingInitialized = true;

            if (ikHandler != null)
                ikHandler.IsActive = true;
        }
    }

    private void SetPosture(PlayerPostureFlags posture)
    {
        currentPosture = posture;
        // Swimming overrides other postures
        bool isSwimming = posture.HasFlag(PlayerPostureFlags.Swim);
        animationHandler?.SetIsSwimming(isSwimming);
        if (isSwimming)
        {
            animationHandler?.SetIsCrouching(false);
            animationHandler?.SetIsSitting(false);
            animationHandler?.SetIsJumping(false);
        }
        else
        {
            animationHandler?.SetIsJumping(posture.HasFlag(PlayerPostureFlags.Jump));
            animationHandler?.SetIsCrouching(posture.HasFlag(PlayerPostureFlags.Crouch));
            animationHandler?.SetIsSitting(posture.HasFlag(PlayerPostureFlags.Sit));
        }
    }

    public void UpdateCar(ushort netId)
    {
        bool willBeOnCar = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar newTrainCar);

        if (OccupiedCar != null)
        {
            if (OccupiedCar == newTrainCar)
                return;

            OccupiedCar.Client_RemovePlayer(this);
            windController?.SetOnCar(null);
        }

        IsOnCar = willBeOnCar && newTrainCar != null;

        if (IsOnCar)
        {
            OccupiedCar = newTrainCar;
            selfTransform.SetParent(OccupiedCar.transform, true);
            OccupiedCar.Client_PlayerOnCar(this);
            windController?.SetOnCar(newTrainCar.TrainCar);
        }
        else
        {
            OccupiedCar = null;
            selfTransform.SetParent(null, true);
        }
    }

    /// <summary>
    /// Attach item to player object when in inventory
    /// </summary>
    /// <param name="itemGo">The item GameObject to attach</param>
    public void AddItemToInventory(GameObject itemGo)
    {
        if (itemGo == null) return;
        ReleaseItem(itemGo);
        if (inventoryRoot == null)
        {
            inventoryRoot = new GameObject("Remote inventory");
            inventoryRoot.transform.SetParent(selfTransform, false);
        }
        itemGo.transform.SetParent(inventoryRoot.transform, true);
        itemGo.SetActive(false);
    }

    /// <summary>
    /// Sets the player's currently held item with optional position and rotation offsets
    /// </summary>
    /// <param name="itemGo">The item GameObject to hold</param>
    /// <param name="targetPos">Optional local position offset</param>
    /// <param name="targetRot">Optional local rotation offset</param>
    /// <param name="rightHand">Indicates if the item is held in the right hand. Always true for nonVR</param>

    public void HoldItem(GameObject itemGo, Vector3? targetPos = null, Quaternion? targetRot = null, bool rightHand = true)
    {
        if (itemGo == null) return;
        var current = rightHand ? RightHandItemGO : LeftHandItemGO;
        if (current != itemGo)
        {
            // A transfer releases the previous hand, while two distinct items remain independent.
            ReleaseItem(itemGo);
            DropItem(rightHand);
            itemGo.SetActive(true);
            itemGo.transform.SetParent(selfTransform, true);
            var grab = itemGo.GetComponentInChildren<GrabHandlerItem>(true);
            var colliders = rightHand ? disabledRHItemColliders : disabledLHItemColliders;
            foreach (Collider col in itemGo.GetComponentsInChildren<Collider>(true))
                if (col != null) colliders.Add(col, col.enabled);
            if (grab != null)
            {
                if (rightHand) rightInteractionAllowed = grab.interactionAllowed;
                else leftInteractionAllowed = grab.interactionAllowed;
                grab.TogglePhysics(false); grab.interactionAllowed = false;
            }
            foreach (var col in colliders.Keys) if (col != null) col.enabled = false;
        }
        if (rightHand) { RightHandItemGO = itemGo; itemHoldPos = targetPos; itemHoldRot = targetRot; }
        else { LeftHandItemGO = itemGo; leftItemHoldPos = targetPos; leftItemHoldRot = targetRot; }
        itemGo.SetActive(true);
    }

    public void DropItem(bool rightHand = true)
    {
        var item = rightHand ? RightHandItemGO : LeftHandItemGO;
        var colliders = rightHand ? disabledRHItemColliders : disabledLHItemColliders;
        var grab = item != null ? item.GetComponentInChildren<GrabHandlerItem>(true) : null;
        if (grab != null)
        {
            grab.TogglePhysics(true);
            grab.interactionAllowed = rightHand ? rightInteractionAllowed : leftInteractionAllowed;
        }
        foreach (var col in colliders) if (col.Key != null) col.Key.enabled = col.Value;
        colliders.Clear();
        if (item != null) item.transform.SetParent(WorldMover.OriginShiftParent, true);
        if (rightHand) { RightHandItemGO = null; itemHoldPos = null; itemHoldRot = null; }
        else { LeftHandItemGO = null; leftItemHoldPos = null; leftItemHoldRot = null; }
    }

    private void UpdateHeldItem(bool rightHand)
    {
        var item = rightHand ? RightHandItemGO : LeftHandItemGO;
        if (item == null) return;
        var offset = rightHand ? itemHoldPos : leftItemHoldPos;
        var rotation = rightHand ? itemHoldRot : leftItemHoldRot;
        if (IsVR && handTrackingInitialized)
        {
            var handPos = rightHand ? currentRightHandWorldPos : currentLeftHandWorldPos;
            var handRot = rightHand ? currentRightHandWorldRot : currentLeftHandWorldRot;
            var worldRot = targetRotation * handRot;
            item.transform.position = selfTransform.position + targetRotation * handPos + worldRot * (offset ?? Vector3.zero);
            item.transform.rotation = worldRot * (rotation ?? Quaternion.identity);
        }
        else
        {
            var anchor = itemAnchorOffset;
            if (!rightHand) anchor.x = -anchor.x;
            item.transform.position = selfTransform.position + selfTransform.TransformDirection(anchor + (offset ?? Vector3.zero));
            item.transform.rotation = selfTransform.rotation * (rotation ?? Quaternion.identity);
        }
    }
    public void ReleaseItem(GameObject item)
    {
        if (item == null) return;
        if (RightHandItemGO == item) DropItem();
        if (LeftHandItemGO == item) DropItem(false);
        if (item.transform.IsChildOf(selfTransform))
            item.transform.SetParent(WorldMover.OriginShiftParent, true);
    }

    public void ReleaseItems()
    {
        DropItem();
        DropItem(false);
        if (inventoryRoot == null) return;
        while (inventoryRoot.transform.childCount > 0)
            inventoryRoot.transform.GetChild(0).SetParent(WorldMover.OriginShiftParent, true);
    }

}

