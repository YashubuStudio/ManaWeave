// Explicitly bounded semantic layer for quantities and richer incantation prose.
// The original WSP and RuneLM weights are unchanged. This class is a parser,
// not a claim that those models learned new operations or arbitrary Japanese.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ManaWeave.Core;

namespace ManaWeave.Magic
{
    internal static class FlexibleChantParser
    {
        private const string Number = @"[-+]?(?:(?:[0-9]+(?:,[0-9]{3})*(?:\.[0-9]+)?|\.[0-9]+)(?:e[+-]?[0-9]+)?|[0-9〇零一二三四五六七八九壱弐参肆伍陸漆柒捌玖十拾百佰千仟万萬億兆]+(?:[点\.][0-9〇零一二三四五六七八九壱弐参肆伍陸漆柒捌玖]+)?)";
        private const string Tail = @"(?:させよ|させて|してください|してくれ|しなさい|せよ|して|する|たまえ|給え|せ|し|よ|て)?";
        private const string MaterialNames = @"魔導合金|魔法合金|魔導結晶|水蒸気|石材|木材|鉄材|土壌|結晶|水晶|合金|蒸気|空気|大気|石|木|鉄|土|水|氷|灰";
        private static readonly SpellOperation[] Operations = {
            SpellOperation.Cool, SpellOperation.Heat, SpellOperation.Excavate,
            SpellOperation.Build, SpellOperation.Charge, SpellOperation.Transmute, SpellOperation.Move
        };
        private static readonly string[] ActionPatterns = {
            @"(?:冷却(?!の理)|冷や|冷ま(?:せ|し)|冷気をもたら|凍てつけ|凍らせ|熱を(?:奪|取り除|抜)|温度を下げ|降温)" + Tail + @"|\b(?:cool|chill)\b|remove\s+heat",
            @"(?:加熱(?!の理)|熱(?:せよ|して|し|せ)|温め|暖め|燃や|焼(?:いて|き払|き尽く|け|き)|灼け|熱を(?:与え|加え|注|流し込)|温度を上げ|昇温)" + Tail + @"|(?<!remove )\b(?:heat|warm)\b",
            @"(?:掘削(?!の理)|掘って|掘れ|掘り(?:抜|出)|くり抜|穿(?:て|ち)|削り取)" + Tail + @"|\b(?:excavate|dig)\b",
            @"(?:創り出|造り出|作り出|作って|構築(?!の理)|築(?:け|き)|形作|建て|設置|配置|生成(?!の理))" + Tail + @"|\b(?:build|construct|create|place)\b",
            @"(?:充填|補充|補給|魔力を(?:注|満た|蓄え)|満ちよ|蓄え)" + Tail + @"|\b(?:charge|infuse|recharge)\b",
            @"(?:錬成(?!の理)|錬金(?:変換|せよ|して)|変成(?:せよ|して)|魔導合金へ変え)" + Tail + @"|\b(?:transmute|alchemize)\b",
            @"(?:移動(?!の理)|動か(?:せ|し)|運(?:べ|んで|び)|持ち上げ|押し下げ|押し出|引き寄せ)" + Tail + @"|\b(?:move|shift|translate)\b"
        };
        private const string ProtectionPattern = @"(?:保護|(?:その)?熱を(?:守れ|護れ)|抵抗を弱め)" + Tail + @"|\bprotect\b";

        private sealed class Action
        {
            public int Index, Length;
            public SpellOperation Operation;
        }

        public static MagicParseResult Parse(string text)
        {
            string danger = Unsupported(text);
            if (danger != null) return Fail(danger);
            string numeric = text.Replace("氷点下", "-").Replace("零下", "-")
                .Replace("マイナス", "-").Replace("−", "-");
            var actions = FindActions(numeric);
            if (actions.Count != 1)
                return Fail(actions.Count == 0 ?
                    "実行する効果を一つ記してください。加熱・冷却・掘削・建築・充填・錬成・移動に対応しています。" :
                    "発動指示が複数あります。同じ効果の繰り返しも含め、一つずつ解析してください。熱の保護時間は同じ指示に指定できます。");
            SpellOperation operation = actions[0].Operation;
            bool thermal = operation == SpellOperation.Heat || operation == SpellOperation.Cool;
            var result = new MagicParseResult {
                Success = true, Source = MagicParser.ExtendedSourceLabel, Confidence = 1f,
                Operation = operation, Radius = 0, Material = MaterialId.Stone,
                Power = thermal ? 120000f : (operation == SpellOperation.Charge ? 50f : 0f),
                Incantation = text
            };
            bool small = Regex.IsMatch(numeric, @"小さ|小規模|狭き|狭い|\b(?:small|gently)\b");
            bool large = Regex.IsMatch(numeric, @"広範囲|広き|広い|大き|大規模|\b(?:large|strongly)\b");
            if (small && large) return Fail("小規模と広範囲が同時に指定されています。範囲を一つにしてください。");
            if (small || large)
            {
                result.Radius = small ? 1 : 3;
                if (thermal) result.Power = small ? 50000f : 300000f;
                else if (operation == SpellOperation.Charge) result.Power = small ? 20f : 80f;
            }

            char[] unconsumed = numeric.ToCharArray();
            MatchCollection radii = Regex.Matches(numeric, @"(?:半径|radius)\s*(?<n>" + Number + @")\s*(?:メートル|m(?![a-z]))?(?=$|[\s、。.!?;（）()「」『』]|の|を|で|に|へ|内|以|と|は|から|まで)");
            if (Regex.IsMatch(numeric, @"半径|\bradius\b") && radii.Count == 0) return Fail("半径はメートルで指定してください。cm・kmなどの換算や未知の単位は省略せず解析を中止しました。");
            if (radii.Count > 1) return Fail("半径の指定が複数あります。一つにしてください。");
            if (radii.Count == 1)
            {
                double radius;
                if (!TryNumber(radii[0].Groups["n"].Value, out radius) || radius != Math.Truncate(radius) || radius < 0 || radius > 5)
                    return Fail("半径は0～5 mの整数です。0 mは照準先の1セルです。");
                result.Radius = (int)radius;
                Mask(unconsumed, radii[0]);
            }
            bool single = Regex.IsMatch(numeric, @"一点|一地点|1セル|一セル|単一セル|単体");
            if (single)
            {
                if ((radii.Count > 0 && result.Radius != 0) || small || large)
                    return Fail("一点と範囲の指定が矛盾しています。一点、または半径を指定してください。");
                result.Radius = 0;
                foreach (Match m in Regex.Matches(numeric, @"一点|一地点|1セル|一セル|単一セル|単体")) Mask(unconsumed, m);
            }

            MatchCollection quantities = Regex.Matches(new string(unconsumed),
                @"(?<n>" + Number + @")\s*(?<u>メガジュール|キロジュール|ジュール|mj|kj|j|°\s*c|℃|celsius|度|c|秒間|秒|seconds?|s|メートル|m|mu|魔力単位)(?![a-z])");
            int energies = 0, temperatures = 0, durations = 0, distances = 0, manaAmounts = 0;
            foreach (Match m in quantities)
            {
                double value;
                if (!TryNumber(m.Groups["n"].Value, out value)) return Fail("数値を解釈できません。例：350℃、200 kJ、十秒、3 m。");
                string unit = Regex.Replace(m.Groups["u"].Value, @"\s+", "");
                if (unit == "j" || unit == "kj" || unit == "mj" || unit.EndsWith("ジュール", StringComparison.Ordinal))
                {
                    energies++;
                    value *= EnergyScale(unit);
                    if (!thermal || value <= 0 || value > 8000000)
                        return Fail("総熱量は加熱・冷却で、0より大きく8 MJ以下を指定してください。");
                    result.Power = (float)value;
                }
                else if (unit == "°c" || unit == "℃" || unit == "c" || unit == "celsius" || unit == "度")
                {
                    temperatures++;
                    string after = numeric.Substring(m.Index + m.Length);
                    string before = numeric.Substring(0, m.Index);
                    if (!Regex.IsMatch(after, @"^\s*(?:まで|に|へ)") &&
                        !Regex.IsMatch(before, @"(?:目標(?:温度)?(?:は|を|:)?|\bto)\s*$"))
                        return Fail("温度は『350℃まで加熱せよ』のように目標値として指定してください。温度差の加算や『二度』という反復回数は未対応です。");
                    if (!thermal || value < -273.15 || value > 5000)
                        return Fail("目標温度は加熱・冷却で、−273.15～5000℃を指定してください。");
                    result.ThermalMode = ThermalMode.Temperature;
                    result.TargetTemperature = (float)value;
                }
                else if (unit == "秒間" || unit == "秒" || unit == "s" || unit.StartsWith("second", StringComparison.Ordinal))
                {
                    durations++;
                    if (!thermal || value < 1 || value > 30)
                        return Fail("熱の保護時間は1～30シミュレーション秒です。");
                    result.DurationSeconds = (float)value;
                }
                else if (unit == "m" || unit == "メートル")
                {
                    if (Regex.IsMatch(numeric.Substring(m.Index + m.Length), @"^\s*(?:先の|先にある|離れた|離れた場所)"))
                        return Fail("距離で離れた対象を選ぶ指定は未対応です。その対象に照準を合わせ、移動する方向と距離を指定してください。");
                    distances++;
                    if (operation != SpellOperation.Move || value != Math.Truncate(value) || value < 1 || value > 8)
                        return Fail("移動距離は1～8 mの整数です。効果範囲には「半径」を付けてください。");
                    result.Distance = (int)value;
                }
                else
                {
                    manaAmounts++;
                    if (operation != SpellOperation.Charge || value <= 0 || value > 10000)
                        return Fail("魔力の充填量は0より大きく10,000 MU以下で指定してください。");
                    result.Power = (float)value;
                }
                Mask(unconsumed, m);
            }
            if (energies > 1 || temperatures > 1 || durations > 1 || distances > 1 || manaAmounts > 1)
                return Fail("同じ種類の数値が複数あります。熱量・温度・時間・距離・魔力量はそれぞれ一つにしてください。");
            if (energies > 0 && temperatures > 0)
                return Fail("総熱量と目標温度は同時に指定できません。どちらか一方を選んでください。");
            if (durations > 0 && !Regex.IsMatch(numeric, ProtectionPattern))
                return Fail("秒数は熱への抵抗を弱める保護時間です。「10秒間保護せよ」のように指定してください。自動で加熱を続ける機能ではありません。");
            if (Regex.IsMatch(numeric, ProtectionPattern) && (!thermal || durations == 0))
                return Fail("熱を保護する場合は、加熱・冷却と1～30秒の保護時間を一緒に指定してください。");
            if (Regex.IsMatch(new string(unconsumed), @"[0-9]|(?:[〇零一二三四五六七八九壱弐参肆伍陸漆柒捌玖十拾百佰千仟万萬億兆]+)\s*(?:[a-z]|ジュール|メガ|カロリー|ワット|回|倍|%|度|秒|メートル|キロ|ミリ|里|光年)"))
                return Fail("使われていない数値や未対応の単位があります。熱量J/kJ/MJ、温度℃、半径m、保護秒数、移動m、充填MUを指定してください。");

            string materialError = ReadMaterials(numeric, result);
            if (materialError != null) return Fail(materialError);
            string selectorText = Regex.Replace(numeric, "(?:" + MaterialNames + @")(?:だけ|のみ)", "");
            if (Regex.IsMatch(selectorText, @"だけ|のみ|以外|除い|除く|避け|except|\bonly\b"))
                return Fail("対象の選別は一種類の素材に対応しています。「鉄だけを」のように指定してください。敵味方や例外条件は未対応です。");

            if (operation == SpellOperation.Move)
            {
                string directionError = ReadDirection(numeric, result);
                if (directionError != null) return Fail(directionError);
            }
            string requestError = UnknownRequests(numeric, actions, thermal);
            if (requestError != null) return Fail(requestError);
            if (result.ThermalMode == ThermalMode.Temperature) result.Power = 0;
            return MagicParser.FinalizeResult(result);
        }

        private static List<Action> FindActions(string text)
        {
            var all = new List<Action>();
            for (int i = 0; i < ActionPatterns.Length; i++)
                foreach (Match m in Regex.Matches(text, ActionPatterns[i], RegexOptions.CultureInvariant))
                    all.Add(new Action { Index = m.Index, Length = m.Length, Operation = Operations[i] });
            all.Sort(delegate(Action a, Action b) {
                int byIndex = a.Index.CompareTo(b.Index);
                return byIndex != 0 ? byIndex : b.Length.CompareTo(a.Length);
            });
            var selected = new List<Action>();
            int end = -1;
            foreach (Action action in all)
            {
                if (action.Index < end) continue;
                selected.Add(action); end = action.Index + action.Length;
            }
            return selected;
        }

        private static string Unsupported(string text)
        {
            if (Regex.IsMatch(text, @"ずつ|それぞれ|各セル|各物質|あたり|当たり"))
                return "熱量・充填量は指定範囲全体の合計です。各セルへ同じ量を配る指定は未対応なので、合計量を指定してください。";
            if (Regex.IsMatch(text, @"今より|現在より|現在の温度から|温度差|度高く|度低く|℃高く|℃低く|°c高く|°c低く|\bby\s*" + Number))
                return "相対的な温度差の指定は未対応です。『350℃まで加熱せよ』のように到達させる目標温度を指定してください。";
            if (Regex.IsMatch(text, @"華氏|ケルビン|絶対温度|fahrenheit|kelvin|[∞√×÷^<>≤≥±]"))
                return "温度は摂氏℃、数量は具体的な数値で指定してください。華氏・ケルビンや数式は現在換算しません。";
            if (Regex.IsMatch(text, @"[?;]|ですか|ますか|できますか|できるか|\b(?:can|could|would)\s+you\b"))
                return "質問・複数命令は実行しません。実行したい効果を一つ、命令として入力してください。";
            // These adjectives can be poetic addresses, not a negated action.
            string negative = text.Replace("果てしない", "").Replace("限りない", "").Replace("揺るがない", "");
            if (Regex.IsMatch(negative, @"ない|なかれ|するな|禁止|せず|さず|かず|ずに|焼くな|冷やすな|動かすな|掘るな|作るな|\b(?:not|don't|never|without|no)\b"))
                return "否定された効果は実行しません。実行する効果を肯定形で一つ指定してください。";
            if (Regex.IsMatch(text, @"もし|場合|したら|すれば|であれば|なければ|なけれ|なら|必ず|絶対|命中|接触|追尾|繰り返|何度|毎秒|毎分|秒後|遅延|永久に|永遠に|無限に|し続け|一定|固定|維持|\b(?:if|unless|then|while|until|repeat|twice|always|infinite|maintain)\b|(?:/|毎)\s*(?:秒|s\b)"))
                return "条件・反復・遅延・温度の自動維持は未対応です。単発の熱操作と、抵抗を弱める保護時間を指定できます。";
            if (Regex.IsMatch(text, @"敵|味方|生物|人間|時間を|時を止|すべて|全て|全部|万物|世界中|全世界|重力|電撃|電流|電圧|電磁|治癒|治療|回復|蘇生|生命|命を|魂を|透明|不可視|転移|瞬間移動|テレポート|爆発|爆破|火球|氷槍|障壁|結界|追撃|連射|召喚|風速|気圧|回転|速度|加速|\b(?:teleport|gravity|heal|enemy|allies|velocity|rotate|all)\b"))
                return "この詠唱には未実装の対象・効果が含まれています。加熱・冷却・掘削・建築・充填・錬成・物質移動の一つを指定してください。";
            // Place words in a poetic address do not select a new target.
            // An actual foot/neighbor/self target is not silently replaced by aim.
            string targetText = Regex.Replace(text, @"[^、。!?\n]*(?:精霊|神々|女神|神|魔力|炎|焔|風|大地|星々|星|光|闇|理|水|氷|石|契約)よ", "");
            targetText = targetText.Replace("目の前", "照準先");
            if (Regex.IsMatch(targetText, @"足元|足下|足もと|頭上|上空の|背後|隣|向こう|向かい|最寄り|近傍|近くの|遠くの|周囲の|周りの|手元|手もと|自分|自身|術者|プレイヤー|相手|あそこ|あちら|こちら|果てまで|指定座標|座標|一番近|最も近|(?:左|右|上|下|前|後ろ)の(?:鉄|石|木|土|水|氷|空気|結晶|対象|物質|セル)"))
                return "対象位置は現在の照準先です。足元・隣・術者自身などの位置選別は未対応なので、その場所へ照準を合わせて指定してください。";
            if (Regex.IsMatch(text, @"(?:nan|infinity|inf)(?=\s|j|mj|kj|mu|c|℃|°|度|ジュール|キロ|メガ|$)"))
                return "有限の数値を指定してください。";
            return null;
        }

        private static string ReadMaterials(string text, MagicParseResult result)
        {
            var ids = new List<MaterialId>();
            if (Regex.IsMatch(text, @"(?:^|[、。\sをのに])(?:黄金|銅|銀|金|鉛|砂利|鉄鉱石|金属|肉|血|酸素|窒素)(?:だけ|のみ)?(?=を|へ|に)"))
                return "指定された素材は現在の物質一覧にありません。鉄・石・木材・水・氷・空気・蒸気・土・灰・魔導結晶・魔導合金を指定してください。";
            if (result.Operation == SpellOperation.Transmute)
            {
                foreach (Match product in Regex.Matches(text, @"(?<p>[^、。\sをへに]+)\s*(?:へ|に)\s*(?=錬成|変成|錬金変換|変え)"))
                {
                    MaterialId productId;
                    if (!TryMaterial(product.Groups["p"].Value, out productId) || productId != MaterialId.Alloy)
                        return "現在の錬成は鉄から魔導合金への変換です。指定された生成物への変換は未対応です。";
                }
            }
            string pattern = @"(?<m>" + MaterialNames + @")(?:の塊)?(?:だけ|のみ)?(?=を|へ|に|で|から|の温度|の熱|と|および|及び)";
            foreach (Match match in Regex.Matches(text, pattern))
            {
                MaterialId id;
                if (!TryMaterial(match.Groups["m"].Value, out id)) continue;
                // This is the reaction product, not a filter for already-made alloy.
                if (result.Operation == SpellOperation.Transmute && id == MaterialId.Alloy &&
                    Regex.IsMatch(text.Substring(match.Index + match.Length), @"^(?:へ|に)")) continue;
                if (!ids.Contains(id)) ids.Add(id);
            }
            foreach (Match match in Regex.Matches(text, @"\b(?:air|soil|stone|wood|water|ice|steam|iron|crystal|alloy|ash)\b"))
            {
                MaterialId id;
                if (TryMaterial(match.Value, out id) && !(result.Operation == SpellOperation.Transmute && id == MaterialId.Alloy) && !ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count > 1) return "素材が複数指定されています。一つの術式では一種類の対象素材、または一種類の生成素材を選んでください。";
            if (result.Operation == SpellOperation.Build)
            {
                if (ids.Count != 1) return "建築する素材を明示してください。例：石を創り出せ、鉄を配置せよ。";
                result.Material = ids[0];
            }
            else if (ids.Count == 1)
            {
                result.HasMaterialFilter = true;
                result.TargetMaterial = ids[0];
                if (result.Operation == SpellOperation.Transmute && ids[0] != MaterialId.Iron)
                    return "現在の錬成は鉄から魔導合金への変換です。鉄を指定してください。";
            }
            return null;
        }

        private static string ReadDirection(string text, MagicParseResult result)
        {
            if (Regex.IsMatch(text, @"斜め|北東|北西|南東|南西|右上|左上|右下|左下|前右|前左|後ろ右|後ろ左|(?:上|下|前|後ろ|東|西|北|南|左|右)(?:と|および|及び)"))
                return "移動は格子の一方向ずつです。上・下・前後左右・東西南北の一つを指定してください。";
            string work = Regex.Replace(text, @"我が(?:前|手|もと)(?:に|へ)(?:集いて|集え|集い)", "");
            var directions = new List<SpellDirection>();
            foreach (Match m in Regex.Matches(work, @"(?<!の)(?<d>上方|下方|前方|後方|後ろ|手前|北|南|東|西|前|右|左|上|下)\s*(?:へ|に|方向)"))
            {
                SpellDirection direction;
                if (TryDirection(m.Groups["d"].Value, out direction)) directions.Add(direction);
            }
            foreach (Match m in Regex.Matches(work, @"\b(?:up|down|north|south|east|west|forward|backward|left|right)\b"))
            {
                SpellDirection direction;
                if (TryDirection(m.Value, out direction)) directions.Add(direction);
            }
            SpellDirection implicitDirection = SpellDirection.Up;
            bool hasImplicit = text.Contains("持ち上げ") || text.Contains("押し下げ") || text.Contains("引き寄せ");
            if (text.Contains("押し下げ")) implicitDirection = SpellDirection.Down;
            if (text.Contains("引き寄せ")) implicitDirection = SpellDirection.Backward;
            if (directions.Count > 1 || (hasImplicit && directions.Count == 1 && directions[0] != implicitDirection))
                return "移動方向が複数または矛盾しています。一方向を指定してください。";
            if (directions.Count == 0 && !hasImplicit)
                return "移動する方向を指定してください。例：対象を上へ3メートル移動させよ。";
            result.Direction = directions.Count == 1 ? directions[0] : implicitDirection;
            return null;
        }

        private static string UnknownRequests(string text, List<Action> actions, bool thermal)
        {
            char[] remainder = text.ToCharArray();
            foreach (Action action in actions)
                for (int i = action.Index; i < action.Index + action.Length; i++) remainder[i] = ' ';
            if (thermal)
                foreach (Match m in Regex.Matches(text, ProtectionPattern)) Mask(remainder, m);
            string work = new string(remainder);
            // Ritual addresses and connective prose are allowed. A second
            // imperative that the executable IR cannot represent is not discarded.
            work = Regex.Replace(work, @"(?:我が|この)?(?:呼び声|声|願い|意|契約|詠唱)(?:に|へ|を)?(?:応え|従い|聞き届け|聞け|受け止め|叶え)" + Tail, "");
            work = Regex.Replace(work, @"(?:我が(?:前|手|もと)(?:に|へ))?(?:集いて|集え|集い|ここに命ずる|我は命ずる|我は願う)", "");
            if (Regex.IsMatch(work, @"せよ|させよ|しろ|してください|しなさい|してくれ|召喚|発射|放て|解き放|降らせ|止め|消せ|消し|照らせ|縛れ|切り裂|刻め|操れ|潰せ|爆ぜ|赤くし|青くし|小さくし|大きくし|眠れ|砕け|消滅|[ぁ-ん一-龯]+(?:え|け|め|べ|て|れ|せ)よ(?=[、。.!\s]|$)|(?:壊|破壊)|\b(?:destroy|stop|erase|summon|shoot|explode)\b"))
                return "解釈できない追加の指示が含まれています。呼びかけは自由に書けますが、実行する効果は対応する一種類にしてください。";
            return null;
        }

        internal static bool TryMaterial(string text, out MaterialId material)
        {
            switch (text)
            {
                case "空気": case "大気": material = MaterialId.Air; return true;
                case "土": case "土壌": material = MaterialId.Soil; return true;
                case "石": case "石材": material = MaterialId.Stone; return true;
                case "木": case "木材": material = MaterialId.Wood; return true;
                case "水": material = MaterialId.Water; return true;
                case "氷": material = MaterialId.Ice; return true;
                case "蒸気": case "水蒸気": material = MaterialId.Steam; return true;
                case "鉄": case "鉄材": material = MaterialId.Iron; return true;
                case "結晶": case "水晶": case "魔導結晶": material = MaterialId.Crystal; return true;
                case "合金": case "魔導合金": case "魔法合金": material = MaterialId.Alloy; return true;
                case "灰": material = MaterialId.Ash; return true;
            }
            if (Regex.IsMatch(text, @"^[a-z]+$") && Enum.TryParse(text, true, out material) && MaterialCatalog.IsValid(material)) return true;
            material = MaterialId.Air; return false;
        }

        internal static bool TryDirection(string text, out SpellDirection direction)
        {
            switch (text)
            {
                case "上": case "上方": direction = SpellDirection.Up; return true;
                case "下": case "下方": direction = SpellDirection.Down; return true;
                case "北": direction = SpellDirection.North; return true;
                case "南": direction = SpellDirection.South; return true;
                case "東": direction = SpellDirection.East; return true;
                case "西": direction = SpellDirection.West; return true;
                case "前": case "前方": direction = SpellDirection.Forward; return true;
                case "後ろ": case "後方": case "手前": direction = SpellDirection.Backward; return true;
                case "左": direction = SpellDirection.Left; return true;
                case "右": direction = SpellDirection.Right; return true;
            }
            if (Regex.IsMatch(text, @"^[a-z]+$") && Enum.TryParse(text, true, out direction) && Enum.IsDefined(typeof(SpellDirection), direction)) return true;
            direction = SpellDirection.Up; return false;
        }

        internal static bool TryScalar(string text, string kind, out double value)
        {
            string units;
            switch (kind)
            {
                case "energy": units = @"メガジュール|キロジュール|ジュール|mj|kj|j"; break;
                case "temperature": units = @"°c|℃|度|c"; break;
                case "duration": units = @"秒間|秒|seconds?|s"; break;
                case "distance": units = @"メートル|m"; break;
                case "mana": units = @"mu|魔力単位"; break;
                default: value = 0; return false;
            }
            Match unit = Regex.Match(text, "(?:" + units + ")$");
            string number = unit.Success ? text.Substring(0, unit.Index) : text;
            if (!TryNumber(number, out value)) return false;
            if (kind == "energy" && unit.Success) value *= EnergyScale(unit.Value);
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static double EnergyScale(string unit)
        { return unit == "mj" || unit == "メガジュール" ? 1000000 : (unit == "kj" || unit == "キロジュール" ? 1000 : 1); }

        private static bool TryNumber(string text, out double value)
        {
            value = 0;
            if (String.IsNullOrEmpty(text)) return false;
            if (Regex.IsMatch(text, @"^[+-]?(?:(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?|\.[0-9]+)(?:e[+-]?[0-9]+)?$"))
                return Double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) &&
                    !Double.IsNaN(value) && !Double.IsInfinity(value);
            int sign = 1;
            if (text[0] == '-' || text[0] == '+') { sign = text[0] == '-' ? -1 : 1; text = text.Substring(1); }
            string[] parts = text.Replace('点', '.').Split('.');
            if (parts.Length > 2 || parts[0].Length == 0 || (parts.Length == 2 && parts[1].Length == 0)) return false;
            double total = 0, section = 0, number = 0, lastSmall = 10000, lastLarge = Double.MaxValue;
            bool hasNumber = false;
            foreach (char ch in parts[0])
            {
                int digit = Digit(ch);
                if (digit >= 0) { number = number * 10 + digit; hasNumber = true; }
                else
                {
                    double unit = ch == '十' || ch == '拾' ? 10 : ch == '百' || ch == '佰' ? 100 : ch == '千' || ch == '仟' ? 1000 : ch == '万' || ch == '萬' ? 10000 : ch == '億' ? 100000000 : ch == '兆' ? 1000000000000 : 0;
                    if (unit == 0) return false;
                    if (unit < 10000)
                    {
                        if (unit >= lastSmall) return false;
                        section += (hasNumber ? number : 1) * unit;
                        lastSmall = unit;
                    }
                    else
                    {
                        if (unit >= lastLarge) return false;
                        double group = section + number;
                        total += (group == 0 ? 1 : group) * unit;
                        section = 0; lastSmall = 10000; lastLarge = unit;
                    }
                    number = 0; hasNumber = false;
                }
                if (total + section + number > 1000000000000) return false;
            }
            value = total + section + number;
            if (parts.Length == 2)
            {
                double place = .1;
                foreach (char ch in parts[1])
                {
                    int digit = Digit(ch); if (digit < 0) return false;
                    value += digit * place; place *= .1;
                }
            }
            value *= sign; return true;
        }

        private static int Digit(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            int index = "〇一二三四五六七八九".IndexOf(ch);
            if (index >= 0) return index;
            switch (ch)
            {
                case '零': return 0; case '壱': return 1; case '弐': return 2; case '参': return 3;
                case '肆': return 4; case '伍': return 5; case '陸': return 6; case '漆': case '柒': return 7;
                case '捌': return 8; case '玖': return 9; default: return -1;
            }
        }

        private static void Mask(char[] text, Match match)
        { for (int i = match.Index; i < match.Index + match.Length; i++) text[i] = ' '; }

        private static MagicParseResult Fail(string error)
        { return new MagicParseResult { Success = false, Error = error, Source = MagicParser.ExtendedSourceLabel, Canonical = "", Confidence = 0 }; }
    }
}
