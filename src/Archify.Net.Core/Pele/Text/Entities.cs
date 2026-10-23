using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    // text/entities.ts
    internal static class Entities
    {
        const string Named =
            "amp:38,lt:60,gt:62,quot:34,apos:39,nbsp:160,iexcl:161,cent:162,pound:163,curren:164,yen:165,sect:167," +
            "copy:169,laquo:171,not:172,reg:174,deg:176,plusmn:177,sup2:178,sup3:179,micro:181,para:182,middot:183," +
            "sup1:185,raquo:187,frac14:188,frac12:189,frac34:190,iquest:191,times:215,divide:247,ndash:8211,mdash:8212," +
            "lsquo:8216,rsquo:8217,sbquo:8218,ldquo:8220,rdquo:8221,bdquo:8222,dagger:8224,Dagger:8225,bull:8226," +
            "hellip:8230,permil:8240,prime:8242,Prime:8243,lsaquo:8249,rsaquo:8250,euro:8364,trade:8482,larr:8592," +
            "uarr:8593,rarr:8594,darr:8595,harr:8596,lArr:8656,uArr:8657,rArr:8658,dArr:8659,hArr:8660,forall:8704," +
            "part:8706,exist:8707,empty:8709,nabla:8711,isin:8712,notin:8713,ni:8715,prod:8719,sum:8721,minus:8722," +
            "radic:8730,infin:8734,and:8743,or:8744,cap:8745,cup:8746,int:8747,there4:8756,sim:8764,cong:8773," +
            "asymp:8776,ne:8800,equiv:8801,le:8804,ge:8805,sub:8834,sup:8835,sube:8838,supe:8839,oplus:8853,otimes:8855," +
            "perp:8869,sdot:8901,loz:9674,spades:9824,clubs:9827,hearts:9829,diams:9830,check:10003,cross:10007," +
            "equals:61,colon:58,semi:59,num:35,percnt:37,lpar:40,rpar:41,lbrack:91,rbrack:93,lbrace:123,rbrace:125," +
            "vert:124,sol:47,bsol:92,quest:63,excl:33,ast:42,plus:43,comma:44,period:46,dollar:36,commat:64,grave:96," +
            "hat:94,lowbar:95,tilde:126,Alpha:913,Beta:914,Gamma:915,Delta:916,Epsilon:917,Zeta:918,Eta:919,Theta:920," +
            "Iota:921,Kappa:922,Lambda:923,Mu:924,Nu:925,Xi:926,Omicron:927,Pi:928,Rho:929,Sigma:931,Tau:932," +
            "Upsilon:933,Phi:934,Chi:935,Psi:936,Omega:937,alpha:945,beta:946,gamma:947,delta:948,epsilon:949,zeta:950," +
            "eta:951,theta:952,iota:953,kappa:954,lambda:955,mu:956,nu:957,xi:958,omicron:959,pi:960,rho:961,sigmaf:962," +
            "sigma:963,tau:964,upsilon:965,phi:966,chi:967,psi:968,omega:969";

        static readonly Dictionary<string, string> NamedMap = Named.Split(',')
            .ToDictionary(pair => pair[..pair.IndexOf(':')], pair => char.ConvertFromUtf32(int.Parse(pair[(pair.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture)), StringComparer.Ordinal);

        static string? Lookup(string name) => NamedMap.GetValueOrDefault(name);

        // entities.ts:33-35. String.fromCodePoint throws a RangeError for a non-integer (a typed `ﬂ°°1.5¶ß`); the bounds check
        // has already passed by then, so only that case reaches it.
        static string CodePoint(double n, string fallback)
        {
            if (!(n > 0 && n <= 0x10ffff && (n < 0xd800 || n > 0xdfff))) return fallback;
            if (n != Math.Floor(n)) throw new JsRangeError($"Invalid code point {Js.Num(n)}");
            return char.ConvertFromUtf32((int)n);
        }

        // /&(?:#(\d+)|#[xX]([0-9a-fA-F]+)|(\w+));/g with JS's ASCII \d and \w.
        static readonly Regex ReEntity = new("&(?:#([0-9]+)|#[xX]([0-9a-fA-F]+)|([A-Za-z0-9_]+));");
        const string Open = "ﬂ°";
        const string Close = "¶ß";

        // Mermaid's placeholders run from `ﬂ°` to `¶ß`, with a second `°` marking a numeric code.
        static string DecodePlaceholders(string text)
        {
            var output = new StringBuilder();
            var last = 0;
            var from = text.IndexOf(Open, StringComparison.Ordinal);
            while (from != -1)
            {
                var stop = Js.IndexOf(text, "¶", from + 2);
                if (stop == -1) break;
                if (Chars.CharAt(text, stop + 1) == 'ß')
                {
                    var numeric = Chars.CharAt(text, from + 2) == '°';
                    var body = text[(from + (numeric ? 3 : 2))..stop];
                    var raw = text[from..(stop + 2)];
                    output.Append(text, last, from - last).Append(numeric ? CodePoint(Js.ToNumber(body), raw) : Lookup(body) ?? "&" + body + ";");
                    last = stop + 2;
                }
                from = Js.IndexOf(text, Open, stop + 1);
            }
            return last == 0 ? text : output.Append(text, last, text.Length - last).ToString();
        }

        // Turns Mermaid's `#35;` / `#quot;` codes (as hidden by EncodeEntities) and HTML entities into characters.
        public static string DecodeEntities(string text)
        {
            if (text.Contains(Close, StringComparison.Ordinal)) text = DecodePlaceholders(text);
            if (text.Contains('&'))
            {
                text = ReEntity.Replace(text, m =>
                {
                    if (m.Groups[1].Success) return CodePoint(Js.ToNumber(m.Groups[1].Value), m.Value);
                    if (m.Groups[2].Success) return CodePoint(Js.ParseInt(m.Groups[2].Value, 16), m.Value);
                    return Lookup(m.Groups[3].Value) ?? m.Value;
                });
            }
            return text;
        }
    }
}
