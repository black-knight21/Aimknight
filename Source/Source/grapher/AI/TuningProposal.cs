using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace grapher.AI
{
    // Treat model output as data. Preserve the complete exported configuration locally.
    internal static class TuningProposal
    {
        private static readonly HashSet<string> Editable = new HashSet<string> {
            "Whole or horizontal accel parameters", "Vertical accel parameters",
            "Output DPI", "Degrees of rotation", "Degrees of angle snapping", "Input Speed Cap"
        };
        private static readonly Dictionary<string, string[]> NumericFields = BuildNumericFields();
        private static readonly HashSet<string> ExtraNumeric = new HashSet<string> { "yRatio", "leftRatio", "upRatio", "domainX", "domainY", "rangeX", "rangeY" };
        private static readonly string[] Modes = { "classic", "jump", "natural", "synchronous", "power", "lut", "noaccel" };
        private static readonly string[] Caps = { "in_out", "input", "output" };

        private static Dictionary<string, string[]> BuildNumericFields()
        {
            var fields = new Dictionary<string, string[]> {
                ["baseMultiplier"] = new[] { "Output DPI" }, ["rotation"] = new[] { "Degrees of rotation" },
                ["snap"] = new[] { "Degrees of angle snapping" }, ["maximumSpeed"] = new[] { "Input Speed Cap" },
                ["yRatio"] = new[] { "Y/X output DPI ratio (vertical sens multiplier)" },
                ["leftRatio"] = new[] { "L/R output DPI ratio (left sens multiplier)" },
                ["upRatio"] = new[] { "U/D output DPI ratio (up sens multiplier)" },
                ["domainX"] = new[] { "Stretches domain for horizontal vs vertical inputs", "x" },
                ["domainY"] = new[] { "Stretches domain for horizontal vs vertical inputs", "y" },
                ["rangeX"] = new[] { "Stretches accel range for horizontal vs vertical inputs", "x" },
                ["rangeY"] = new[] { "Stretches accel range for horizontal vs vertical inputs", "y" }
            };
            foreach (string axis in new[] { "x", "y" })
            {
                string key = axis == "x" ? "Whole or horizontal accel parameters" : "Vertical accel parameters";
                foreach (string field in new[] { "inputOffset", "outputOffset", "acceleration", "decayRate", "gamma", "motivity", "exponentClassic", "scale", "exponentPower", "limit", "syncSpeed", "smooth" })
                    fields[axis + "." + field] = new[] { key, field };
                fields[axis + ".capInput"] = new[] { key, "Cap / Jump", "x" };
                fields[axis + ".capOutput"] = new[] { key, "Cap / Jump", "y" };
            }
            return fields;
        }

        // Gemini only returns small edit records. Driver serialization names and unrelated fields stay local.
        public static JObject ResponseSchema()
        {
            Func<string[], JObject> stringEnum = values => new JObject { ["type"] = "STRING", ["enum"] = new JArray(values) };
            Func<JObject, JObject> records = properties => new JObject {
                ["type"] = "ARRAY", ["maxItems"] = 64,
                ["items"] = new JObject { ["type"] = "OBJECT", ["properties"] = properties, ["required"] = new JArray(properties.Properties().Select(p => p.Name)) }
            };
            return new JObject {
                ["type"] = "OBJECT", ["required"] = new JArray("changes", "modes", "caps"),
                ["properties"] = new JObject {
                    ["changes"] = records(new JObject { ["field"] = stringEnum(NumericFields.Keys.ToArray()), ["value"] = new JObject { ["type"] = "NUMBER" } }),
                    ["modes"] = records(new JObject { ["axis"] = stringEnum(new[] { "x", "y" }), ["mode"] = stringEnum(Modes) }),
                    ["caps"] = records(new JObject { ["axis"] = stringEnum(new[] { "x", "y" }), ["mode"] = stringEnum(Caps) })
                }
            };
        }

        public static JObject TuningValues(JObject baseline)
        {
            var profile = baseline["profiles"][0]; var values = new JObject();
            foreach (var field in NumericFields)
            {
                JToken value = profile;
                foreach (string part in field.Value) value = value[part];
                values[field.Key] = field.Key == "baseMultiplier" ? new JValue(value.Value<double>() / ScaleDpi(baseline)) : value.DeepClone();
            }
            values["x.gain"] = profile["Whole or horizontal accel parameters"]["Gain / Velocity"].DeepClone();
            values["y.gain"] = profile["Vertical accel parameters"]["Gain / Velocity"].DeepClone();
            values["combinedAxes"] = profile["Input speed calculation parameters"]["Whole/combined accel (set false for 'by component' mode)"].DeepClone();
            return values;
        }
        private static double ScaleDpi(JObject baseline)
        {
            double dpi = (double)baseline["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"];
            return dpi > 0 ? dpi : 1000;
        }

        public static JObject ApplyChanges(JObject baseline, JObject edits)
        {
            if (edits == null || !edits.Properties().Any()) throw new ArgumentException("The AI returned no edit document. Retry the request.");
            edits = (JObject)edits.DeepClone();
            Explanation(edits); edits.Remove("explanation");
            GameAdvice(edits);edits.Remove("gameAdvice");
            var switches = edits["switches"] ?? new JArray(); edits.Remove("switches");
            if(!(switches is JArray) || switches.Count()>3)throw new ArgumentException("Invalid curve switch list.");
            foreach (string group in new[] { "changes", "modes", "caps" })
                if (edits.Property(group) == null) edits[group] = new JArray();
            RequireFields(edits, "changes", "modes", "caps");
            var profile = (JObject)baseline["profiles"][0].DeepClone();
            var seen = new HashSet<string>();
            var extraNumbers = new Dictionary<string,double>();
            foreach (var token in Records(edits, "changes"))
            {
                var edit = token as JObject; RequireFields(edit, "field", "value");
                string field = edit["field"].Type == JTokenType.String ? (string)edit["field"] : null;
                if (field == null || !NumericFields.ContainsKey(field) || !seen.Add(field))
                    throw new ArgumentException("The AI proposed an unsupported or repeated setting. Retry the request.");
                var number = edit["value"];
                if (number.Type != JTokenType.Integer && number.Type != JTokenType.Float)
                    throw new ArgumentException("The AI returned a non-numeric setting. Retry the request.");
                double value = number.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("The AI returned an invalid number.");
                if(ExtraNumeric.Contains(field)) { if(value<=0 || value>100)throw new ArgumentException("Directional scales must be positive and at most 100."); extraNumbers.Add(field,value);continue; }
                if (field == "baseMultiplier")
                {
                    if (value <= 0 || value > 10) throw new ArgumentException("The AI proposed a base multiplier outside the supported range (0–10×).");
                    value *= ScaleDpi(baseline);
                }
                var path = NumericFields[field]; JToken target = profile;
                for (int i = 0; i < path.Length - 1; i++) target = target[path[i]];
                target[path[path.Length - 1]] = value;
            }
            foreach (string group in new[] { "modes", "caps" })
            {
                var axes = new HashSet<string>();
                foreach (var token in Records(edits, group))
                {
                    var edit = token as JObject; RequireFields(edit, "axis", "mode");
                    string axis = edit["axis"].Type == JTokenType.String ? (string)edit["axis"] : null;
                    string mode = edit["mode"].Type == JTokenType.String ? (string)edit["mode"] : null;
                    if ((axis != "x" && axis != "y") || !axes.Add(axis) || !(group == "modes" ? Modes : Caps).Contains(mode))
                        throw new ArgumentException("The AI proposed an unsupported or repeated curve mode. Retry the request.");
                    string key = axis == "x" ? "Whole or horizontal accel parameters" : "Vertical accel parameters";
                    profile[key][group == "modes" ? "mode" : "Cap mode"] = mode;
                }
            }
            var result = Merge(baseline, profile);
            foreach(var extra in extraNumbers) { var path=NumericFields[extra.Key];JToken target=result["profiles"][0];for(int i=0;i<path.Length-1;i++)target=target[path[i]];target[path[path.Length-1]]=extra.Value; }
            var seenSwitches=new HashSet<string>();
            foreach(var token in switches) {
                var edit=token as JObject;RequireFields(edit,"field","value");string field=edit["field"].Type==JTokenType.String?(string)edit["field"]:null;
                if(field==null || !new[]{"x.gain","y.gain","combinedAxes"}.Contains(field) || !seenSwitches.Add(field) || edit["value"].Type!=JTokenType.Boolean)throw new ArgumentException("Unsupported, repeated or malformed curve switch.");
                var target=result["profiles"][0];
                if(field=="combinedAxes")target["Input speed calculation parameters"]["Whole/combined accel (set false for 'by component' mode)"]=edit["value"].DeepClone();
                else target[field=="x.gain"?"Whole or horizontal accel parameters":"Vertical accel parameters"]["Gain / Velocity"]=edit["value"].DeepClone();
            }
            var converted=DriverConfig.Convert(result.ToString());if(converted.Item2!=null)throw new ArgumentException(converted.Item2);
            return result;
        }
        private static JArray Records(JObject document, string group)
        {
            var records = document[group] as JArray;
            if (records == null || records.Count > 64) throw new ArgumentException("The AI returned an invalid edit list. Retry the request.");
            return records;
        }
        internal static string Explanation(JObject document)
        {
            var value = document["explanation"];
            if (value == null) return "";
            if (value.Type != JTokenType.String || value.Value<string>().Length > 1200) throw new ArgumentException("The AI returned an invalid explanation. Retry the request.");
            return value.Value<string>().Trim();
        }
        internal static string GameAdvice(JObject document)
        {
            var raw=document["gameAdvice"];if(raw==null)return "";
            var advice=raw as JObject;RequireFields(advice,"suggestedSensitivity","reason","testPlan");
            var value=advice["suggestedSensitivity"];
            if(value.Type!=JTokenType.Null&&(value.Type!=JTokenType.Float&&value.Type!=JTokenType.Integer||double.IsNaN((double)value)||double.IsInfinity((double)value)||(double)value<=0||(double)value>100))throw new ArgumentException("Invalid game sensitivity suggestion.");
            foreach(string field in new[]{"reason","testPlan"})if(advice[field].Type!=JTokenType.String||((string)advice[field]).Length>1500)throw new ArgumentException("Invalid coaching advice.");
            return (value.Type==JTokenType.Null?"Game sensitivity: keep current / confirm game first.":"Suggested in-game sensitivity: "+((double)value).ToString("0.#####",System.Globalization.CultureInfo.InvariantCulture)+" (manual change only).")+"\n"+(string)advice["reason"]+"\nTest plan: "+(string)advice["testPlan"];
        }
        private static void RequireFields(JObject obj, params string[] fields)
        {
            if (obj == null || !new HashSet<string>(obj.Properties().Select(p => p.Name)).SetEquals(fields))
                throw new ArgumentException("The AI returned an unsupported edit format. Retry the request; no settings were changed.");
        }

        public static JObject Parse(string json)
        {
            if (json.Length > 256 * 1024) throw new ArgumentException("Settings response is too large.");
            return JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }

        public static JObject Merge(JObject baseline, JObject profile)
        {
            var result = (JObject)baseline.DeepClone();
            var original = (JObject)baseline["profiles"][0];
            CheckShape(original, profile, "profile");
            foreach (var property in original.Properties())
                if (!Editable.Contains(property.Name) && !JToken.DeepEquals(property.Value, profile[property.Name]))
                    throw new ArgumentException("AI cannot change " + property.Name + ".");
            result["profiles"][0] = profile.DeepClone();
            var converted = DriverConfig.Convert(result.ToString());
            if (converted.Item2 != null) throw new ArgumentException(converted.Item2);
            return result;
        }

        public static JObject LoadCurve(JObject baseline, JObject saved)
        {
            var validated = DriverConfig.Convert(saved.ToString());
            if (validated.Item2 != null) throw new ArgumentException(validated.Item2);
            var profile = (JObject)baseline["profiles"][0].DeepClone();
            foreach (string key in Editable) profile[key] = saved["profiles"][0][key].DeepClone();
            profile["Output DPI"] = (double)profile["Output DPI"] / ScaleDpi(saved) * ScaleDpi(baseline);
            return Merge(baseline, profile);
        }

        private static void CheckShape(JToken original, JToken candidate, string path)
        {
            if (candidate == null) throw new ArgumentException("Missing " + path);
            if (original is JObject obj)
            {
                var other = candidate as JObject;
                if (other == null || !new HashSet<string>(obj.Properties().Select(p => p.Name))
                    .SetEquals(other.Properties().Select(p => p.Name)))
                    throw new ArgumentException("Settings fields do not match at " + path);
                foreach (var p in obj.Properties()) CheckShape(p.Value, other[p.Name], path + "." + p.Name);
            }
            else if (original is JArray)
            {
                // LUT editing needs a separate graph editor; preserve its data for this first version.
                if (!JToken.DeepEquals(original, candidate)) throw new ArgumentException("AI cannot edit table data.");
            }
            else if (original.Type == JTokenType.Integer || original.Type == JTokenType.Float)
            {
                if (candidate.Type != JTokenType.Integer && candidate.Type != JTokenType.Float)
                    throw new ArgumentException("Expected a number at " + path);
                double value = candidate.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Non-finite number at " + path);
            }
            else if (original.Type != candidate.Type) throw new ArgumentException("Wrong value type at " + path);
        }

        public static string Diff(JToken before, JToken after, string path = "")
        {
            if (JToken.DeepEquals(before, after)) return "";
            if (before is JObject obj)
                return string.Concat(obj.Properties().Select(p => Diff(p.Value, after[p.Name], path + "/" + p.Name)));
            if (before is JArray array)
                return string.Concat(array.Select((v, i) => Diff(v, after[i], path + "/" + i)));
            return path + ": " + before + " -> " + after + Environment.NewLine;
        }
    }
}
