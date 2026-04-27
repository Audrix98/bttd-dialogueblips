using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using RedBlueGames.Tools.TextTyper;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

[BepInPlugin("bttd.dialogueblips", "Dialogue Blips", "1.0.0")]
public class DialogueBlipsPlugin : BasePlugin
{
    private const int DialogueBlipsOptionId = 900001;
    private const string BlipSoundKey = "tab_switch";
    private const float BlipCooldown = 0.03f;

    private static float lastBlip = 0f;
    private static bool blipsEnabled = true;
    private static ConfigEntry<bool> cfgEnabled;

    private static readonly HashSet<IntPtr> hookedTypers = new HashSet<IntPtr>();
    private static readonly HashSet<IntPtr> injectedOptionsMenus = new HashSet<IntPtr>();

    public override void Load()
    {
        cfgEnabled = Config.Bind(
            "General",
            "EnableDialogueBlips",
            true,
            "Enable or disable dialogue blip sounds."
        );

        blipsEnabled = cfgEnabled.Value;

        var harmony = new Harmony("bttd.dialogueblips");

        PatchIfFound(
            harmony,
            typeof(TalkWord),
            "Text_TypeText",
            prefixName: nameof(TextTypeText_Prefix)
        );

        PatchIfFound(
            harmony,
            typeof(UI_Options),
            "Start",
            postfixName: nameof(UIOptions_Start_Postfix)
        );

        Log.LogInfo("Dialogue Blips loaded successfully.");
    }

    private static void PatchIfFound(
        Harmony harmony,
        Type type,
        string methodName,
        string prefixName = null,
        string postfixName = null
    )
    {
        var target = AccessTools.Method(type, methodName);

        if (target == null)
        {
            LogWarning("Could not find method: " + type.Name + "." + methodName);
            return;
        }

        var prefix = prefixName == null
            ? null
            : new HarmonyMethod(AccessTools.Method(typeof(DialogueBlipsPlugin), prefixName));

        var postfix = postfixName == null
            ? null
            : new HarmonyMethod(AccessTools.Method(typeof(DialogueBlipsPlugin), postfixName));

        harmony.Patch(target, prefix: prefix, postfix: postfix);
    }

    private static void TextTypeText_Prefix(TalkWord __instance)
    {
        if (__instance == null) return;
        if (__instance.textTyper == null) return;
        if (__instance.textTyper.characterPrinted == null) return;

        var typer = __instance.textTyper;
        IntPtr ptr = typer.Pointer;

        if (hookedTypers.Contains(ptr)) return;
        hookedTypers.Add(ptr);

        var act = DelegateSupport.ConvertDelegate<UnityAction<string>>(
            (Action<string>)((s) =>
            {
                if (!blipsEnabled) return;
                if (string.IsNullOrEmpty(s)) return;

                char c = s[0];

                if (!char.IsLetterOrDigit(c)) return;

                if (Time.unscaledTime - lastBlip < BlipCooldown) return;
                lastBlip = Time.unscaledTime;

                if (AudioManage.singleton != null)
                {
                    AudioManage.singleton.PlaySound2(BlipSoundKey);
                }
            })
        );

        typer.characterPrinted.AddListener(act);
    }

    private static void UIOptions_Start_Postfix(UI_Options __instance)
    {
        InjectOptionsToggle(__instance);
    }

    private static void InjectOptionsToggle(UI_Options options)
    {
        try
        {
            if (options == null) return;

            IntPtr ptr = options.Pointer;
            if (injectedOptionsMenus.Contains(ptr)) return;
            injectedOptionsMenus.Add(ptr);

            var template = options.GetOptionsUnit((UI_OptionsType)10);
            if (template == null)
            {
                LogWarning("Could not find options toggle template.");
                return;
            }

            var cloneGo = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            cloneGo.name = "Dialogue Blips";

            var newUnit = cloneGo.GetComponent<UI_OptionsUnit>();
            if (newUnit != null)
            {
                newUnit.name = "Dialogue Blips";
                newUnit.optionsType = (UI_OptionsType)DialogueBlipsOptionId;
            }

            SetLabelText(cloneGo, "Dialogue Blips");
            SetupToggle(cloneGo);
            FixInjectedPosition(template.gameObject, cloneGo);
        }
        catch (Exception e)
        {
            LogWarning("Options UI injection failed: " + e);
        }
    }

    private static void SetLabelText(GameObject cloneGo, string text)
    {
        var labelTransform = cloneGo.transform.Find("Text");
        if (labelTransform == null) return;

        var label = labelTransform.GetComponent<Text>();
        if (label != null)
        {
            label.text = text;
        }
    }

    private static void SetupToggle(GameObject cloneGo)
    {
        var toggleTransform = cloneGo.transform.Find("right/Toggle");
        if (toggleTransform == null)
        {
            LogWarning("Could not find Dialogue Blips toggle object.");
            return;
        }

        var toggle = toggleTransform.GetComponent<Toggle>();
        if (toggle == null)
        {
            LogWarning("Could not find Dialogue Blips toggle component.");
            return;
        }

        toggle.onValueChanged.RemoveAllListeners();
        toggle.SetIsOnWithoutNotify(blipsEnabled);

        var toggleAction = DelegateSupport.ConvertDelegate<UnityAction<bool>>(
            (Action<bool>)((value) =>
            {
                blipsEnabled = value;

                if (cfgEnabled != null)
                {
                    cfgEnabled.Value = value;
                    cfgEnabled.ConfigFile.Save();
                }
            })
        );

        toggle.onValueChanged.AddListener(toggleAction);
    }

    private static void FixInjectedPosition(GameObject templateGo, GameObject cloneGo)
    {
        var templateRt = templateGo.GetComponent<RectTransform>();
        var cloneRt = cloneGo.GetComponent<RectTransform>();

        if (templateRt == null || cloneRt == null) return;

        cloneRt.anchorMin = templateRt.anchorMin;
        cloneRt.anchorMax = templateRt.anchorMax;
        cloneRt.pivot = templateRt.pivot;
        cloneRt.sizeDelta = templateRt.sizeDelta;
        cloneRt.localScale = templateRt.localScale;
        cloneRt.localRotation = templateRt.localRotation;

        float lowestY = templateRt.anchoredPosition.y;
        float spacing = 80f;

        var parent = templateRt.parent;
        if (parent != null)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null || child.gameObject == cloneGo) continue;

                var childRt = child.GetComponent<RectTransform>();
                if (childRt == null) continue;

                if (childRt.anchoredPosition.y < lowestY)
                    lowestY = childRt.anchoredPosition.y;
            }
        }

        cloneRt.anchoredPosition = new Vector2(
            templateRt.anchoredPosition.x,
            lowestY - spacing
        );
    }

    private static void LogWarning(string message)
    {
        Debug.LogWarning("[Dialogue Blips] " + message);
    }
}