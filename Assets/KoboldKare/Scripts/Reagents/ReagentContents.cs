using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ExitGames.Client.Photon;
using SimpleJSON;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Serialization;

[System.Serializable]
public class Reagent {
    public short id;
    public float volume;
}

public class ReagentContents : IEnumerable<Reagent> {
    public delegate void ReagentContentsChangedAction(ReagentContents contents);

    public ReagentContentsChangedAction changed;
    public ReagentContents(ReagentContents other) { Copy(other); }
    public ReagentContents(float maxVolume = float.MaxValue) {
        this.maxVolume = maxVolume;
    }

    public void Copy(ReagentContents other) {
        contents = new Dictionary<short, Reagent>(other.contents);
        maxVolume = other.maxVolume;
    }

    private float maxVolume = float.MaxValue;
    public float GetMaxVolume() {
        return maxVolume;
    }
    public void SetMaxVolume(float newMaxVolume) {
        if (Math.Abs(maxVolume - newMaxVolume) < 0.001f) {
            return;
        }

        maxVolume = newMaxVolume;
        if (contents != null && maxVolume < volume) {
            Spill(volume-maxVolume);
        }
        changed?.Invoke(this);
    }
    private const float metabolizationVolumeEpsilon = 0.1f;
    public float volume {
        get {
            float v = 0f;
            foreach(var pair in contents) {
                v += pair.Value.volume;
            }
            return v;
        }
    }
    public int Count => contents.Count;
    [SerializeField]
    private Dictionary<short,Reagent> contents = new Dictionary<short,Reagent>();
    public void OverrideReagent(short id, float volume) {
        if (contents.ContainsKey(id)) {
            contents[id].volume = volume;
            return;
        }
        contents.Add(id, new Reagent(){ id=id, volume=volume });
        changed?.Invoke(this);
    }
    public void AddMix(short id, float addVolume, GenericReagentContainer worldContainer = null) {
        if (contents.ContainsKey(id)) {
            contents[id].volume = Mathf.Max(0f,contents[id].volume+addVolume);
        } else {
            contents.Add(id, new Reagent() {id=id,volume=addVolume});
            if (worldContainer != null) {
                //ReagentDatabase.GetReagent(id).onExist.Invoke(worldContainer);
            }
        }
        if (worldContainer != null) {
            ReagentDatabase.DoReactions(worldContainer, id);
        }
        if (volume > maxVolume) {
            Spill(volume-maxVolume);
        }
        changed?.Invoke(this);
    }
    public void AddMix(Reagent reagent, GenericReagentContainer worldContainer = null) {
        AddMix(reagent.id, reagent.volume, worldContainer);
    }
    public void AddMix(ReagentContents container, GenericReagentContainer worldContainer = null) {
        foreach(var pair in container.contents) {
            AddMix(pair.Value, worldContainer);
        }
    }
    public ReagentContents Spill(float spillVolume) {
        float v = volume;
        ReagentContents spillContents = new ReagentContents();
        if (v <= 0f) {
            return spillContents;
        }
        float spillRatio = Mathf.Clamp01(spillVolume/v);
        
        foreach(var pair in contents) {
            spillContents.AddMix(pair.Key, pair.Value.volume*spillRatio);
            contents[pair.Key].volume = pair.Value.volume*(1f-spillRatio);
        }
        changed?.Invoke(this);
        return spillContents;
    }
    public void Clear() {
        contents.Clear();
        changed?.Invoke(this);
    }

    public ReagentContents Metabolize(float deltaTime) {
        float v = volume;
        ReagentContents metabolizeContents = new ReagentContents();
        if (v <= 0f) {
            return metabolizeContents;
        }
        foreach(var pair in contents) {
            float metabolizationHalfLife = ReagentDatabase.GetReagent(pair.Key).GetMetabolizationHalfLife();
            float metaHalfLife = metabolizationHalfLife == 0 ? pair.Value.volume : pair.Value.volume * Mathf.Pow(0.5f, deltaTime / metabolizationHalfLife);
            // halflife on a tiny value suuucks, just kill it if the value gets small enough so we don't spam incredibly tiny updates.
            if (pair.Value.volume <= metabolizationVolumeEpsilon) {
                metabolizeContents.AddMix(pair.Value);
                contents[pair.Key].volume = 0f;
                continue;
            }
            float loss = Mathf.Max(pair.Value.volume - metaHalfLife, 0f);
            contents[pair.Key].volume = Mathf.Max(metaHalfLife, 0f);
            metabolizeContents.AddMix(pair.Key, loss);
        }
        changed?.Invoke(this);
        return metabolizeContents;
    }

    public float GetVolumeOf(short id) {
        if (contents.ContainsKey(id)) {
            return contents[id].volume;
        }
        return 0f;
    }
    public float GetVolumeOf(ScriptableReagent reagent) {
        short id = ReagentDatabase.GetID(reagent);
        return GetVolumeOf(id);
    }
    public bool IsCleaningAgent() {
        float totalCleanerVolume = 0f;
        foreach(var pair in contents) {
            if (ReagentDatabase.GetReagent(pair.Key).IsCleaningAgent()) {
                totalCleanerVolume += pair.Value.volume;
            }
        }
        // If we're majorly a cleaning agent...
        return totalCleanerVolume >= volume*0.5f;
    }

    public float GetCalories() {
        float totalCalories = 0f;
        foreach(var pair in contents) {
            totalCalories += pair.Value.volume * ReagentDatabase.GetReagent(pair.Key).GetCalories();
        }
        return totalCalories;
    }

    public Color GetColor() {
        float v = volume;
        if (v <= 0f) {
            return Color.white;
        }
        Color totalColor = Color.black;
        foreach(var pair in contents) {
            if (pair.Value.volume <= 0f) {
                continue;
            }
            totalColor += ReagentDatabase.GetReagent(pair.Key).GetColor()*((pair.Value.volume)/v);
        }
        return totalColor;
    }
    public float GetValue() {
        float totalValue = 0f;
        foreach(var pair in contents) {
            totalValue += ReagentDatabase.GetReagent(pair.Key).GetValue()*pair.Value.volume;
        }
        return totalValue;
    }
    public static short SerializeReagentContents(StreamBuffer outStream, object customObject) {
        ReagentContents reagentContents = (ReagentContents)customObject;
        short size = (short)(sizeof(float)+(sizeof(short) + sizeof(float)) * reagentContents.contents.Count);
        byte[] bytes = new byte[size];
        int index = 0;
        Protocol.Serialize(reagentContents.maxVolume, bytes, ref index);
        foreach (KeyValuePair<short, Reagent> pair in reagentContents.contents) {
            if (pair.Value.volume <= 0f) {
                continue;
            }
            Protocol.Serialize((short)pair.Key, bytes, ref index);
            Protocol.Serialize(pair.Value.volume, bytes, ref index);
        }
        outStream.Write(bytes, 0, size);
        return size;
    }
    public static object DeserializeReagentContents(StreamBuffer inStream, short length) {
        ReagentContents reagentContents = new ReagentContents();
        byte[] bytes = new byte[length];
        inStream.Read(bytes, 0, length);
        int index = 0;
        Protocol.Deserialize(out float maxVolume, bytes, ref index);
        reagentContents.maxVolume = maxVolume;
        while (index < length) {
            short id = 0;
            float volume = 0;
            Protocol.Deserialize(out id, bytes, ref index);
            Protocol.Deserialize(out volume, bytes, ref index);
            reagentContents.OverrideReagent(id, volume);
        }
        return reagentContents;
    }
    public void Save(JSONNode node, string key) {
        JSONNode rootNode = JSONNode.Parse("{}");
        rootNode["maxVolume"] = maxVolume;
        JSONArray reagents = new JSONArray();
        foreach (KeyValuePair<short, Reagent> pair in contents) {
            JSONNode reagentPair = JSONNode.Parse("{}");
            if (pair.Value.volume <= 0f) {
                continue;
            }

            reagentPair["name"] = ReagentDatabase.GetReagent(pair.Key).name;
            reagentPair["volume"] = pair.Value.volume;
            reagents.Add(reagentPair);
        }
        rootNode["reagents"] = reagents;
        node[key] = rootNode;
    }
    public void Load(JSONNode node, string key) {
        JSONNode rootNode = node[key];
        Clear();
        maxVolume = rootNode["maxVolume"];
        JSONArray reagents = rootNode["reagents"].AsArray;
        for(int i=0;i<reagents.Count;i++) {
            string name = reagents[i]["name"];
            float vol = reagents[i]["volume"];
            OverrideReagent(ReagentDatabase.GetID(ReagentDatabase.GetReagent(name)), vol);
        }
    }

    public IEnumerator<Reagent> GetEnumerator() {
        foreach (var keyValuePair in contents) {
            yield return keyValuePair.Value;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }
}
