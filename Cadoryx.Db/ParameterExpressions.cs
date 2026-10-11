using System.Collections.Immutable;
using System.Globalization;

namespace Cadoryx.Db;

public enum ParameterDimension { Scalar=0, Length=1, Angle=2 }
public readonly record struct ParameterQuantity(double Value,int LengthPower=0,int AnglePower=0)
{
    public static ParameterQuantity Of(double value,ParameterDimension dimension)=>dimension switch
    {ParameterDimension.Scalar=>new(value),ParameterDimension.Length=>new(value,1),ParameterDimension.Angle=>new(value,0,1),_=>throw new CadValidationException("Unknown parameter dimension.")};
    public bool HasDimension(ParameterDimension dimension)=>this with{Value=0}==Of(0,dimension);
}

/// <summary>Bounded arithmetic only; expressions cannot execute code or access arbitrary properties.</summary>
public static class ParameterExpressions
{
    private static readonly ImmutableDictionary<string,ParameterQuantity> Constants=new Dictionary<string,ParameterQuantity>(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"]=new(Math.PI),["mm"]=new(1,1),["cm"]=new(10,1),["m"]=new(1000,1),["in"]=new(25.4,1),["ft"]=new(304.8,1),
        ["rad"]=new(1,0,1),["deg"]=new(Math.PI/180,0,1)
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Functions=["abs","sqrt","sin","cos","tan","min","max"];
    public static bool IsName(string name)=>name is {Length:>0 and <=64}&&(char.IsAsciiLetter(name[0])||name[0]=='_')&&
        name.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_')&&!Constants.ContainsKey(name)&&!Functions.Contains(name,StringComparer.OrdinalIgnoreCase);
    public static ParameterQuantity Evaluate(string expression,Func<string,ParameterQuantity>? lookup=null)
        =>new Parser(expression,lookup??(name=>throw new CadValidationException($"Unknown parameter: {name}."))).Parse();
    public static ImmutableArray<string> Dependencies(string expression)
    {
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Parsing and dimension checks happen during evaluation; this lexical pass only gathers identifier dependencies.
        var scanner=new Parser(expression,_=>new());
        while(scanner.Token.Kind!=Kind.End)
        {
            if(scanner.Token.Kind==Kind.Name&&!Constants.ContainsKey(scanner.Token.Text)&&!Functions.Contains(scanner.Token.Text,StringComparer.OrdinalIgnoreCase))names.Add(scanner.Token.Text);
            scanner.Next();
        }
        return names.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }
    private enum Kind { End, Number, Name, Plus, Minus, Times, Divide, Open, Close, Comma }
    private readonly record struct Token(Kind Kind,string Text,double Number=0);
    private sealed class Parser
    {
        private readonly string input;
        private readonly Func<string,ParameterQuantity> lookup;
        private int position,tokens,depth;
        public Token Token {get;private set;}
        public Parser(string text,Func<string,ParameterQuantity> lookup)
        {
            if(string.IsNullOrWhiteSpace(text)||text.Length>2048)throw new CadValidationException("Expression must contain 1–2048 characters.");
            input=text;this.lookup=lookup;Next();
        }
        public void Next()
        {
            if(++tokens>512)throw new CadValidationException("Expression has too many tokens.");
            while(position<input.Length&&char.IsWhiteSpace(input[position]))position++;
            if(position==input.Length){Token=new(Kind.End,"");return;}
            int start=position;char c=input[position++];
            if(char.IsAsciiDigit(c)||c=='.')
            {
                while(position<input.Length&&(char.IsAsciiDigit(input[position])||input[position]=='.'))position++;
                if(position<input.Length&&input[position] is 'e' or 'E')
                {position++;if(position<input.Length&&input[position] is '+' or '-')position++;while(position<input.Length&&char.IsAsciiDigit(input[position]))position++;}
                string text=input[start..position];
                if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value))throw Error("Invalid number");
                Token=new(Kind.Number,text,value);return;
            }
            if(char.IsAsciiLetter(c)||c=='_')
            {
                while(position<input.Length&&(char.IsAsciiLetterOrDigit(input[position])||input[position]=='_'))position++;
                Token=new(Kind.Name,input[start..position]);return;
            }
            Token=new(c switch{'+'=>Kind.Plus,'-'=>Kind.Minus,'*'=>Kind.Times,'/'=>Kind.Divide,'('=>Kind.Open,')'=>Kind.Close,','=>Kind.Comma,_=>throw Error("Invalid character")},c.ToString());
        }
        public ParameterQuantity Parse(){var result=Sum();if(Token.Kind!=Kind.End)throw Error("Unexpected token");return Check(result);}
        private ParameterQuantity Sum()
        {
            var result=Product();
            while(Token.Kind is Kind.Plus or Kind.Minus)
            {
                var op=Token.Kind;Next();var right=Product();Same(result,right);
                result=Check(result with{Value=op==Kind.Plus?result.Value+right.Value:result.Value-right.Value});
            }
            return result;
        }
        private ParameterQuantity Product()
        {
            var result=Unary();
            while(Token.Kind is Kind.Times or Kind.Divide)
            {
                var op=Token.Kind;Next();var right=Unary();
                if(op==Kind.Divide&&right.Value==0)throw Error("Division by zero");
                result=Check(new(op==Kind.Times?result.Value*right.Value:result.Value/right.Value,
                    result.LengthPower+(op==Kind.Times?right.LengthPower:-right.LengthPower),result.AnglePower+(op==Kind.Times?right.AnglePower:-right.AnglePower)));
            }
            return result;
        }
        private ParameterQuantity Unary()
        {
            if(++depth>64)throw Error("Expression nesting is too deep");
            try
            {
                if(Token.Kind is Kind.Plus or Kind.Minus){bool negative=Token.Kind==Kind.Minus;Next();var value=Unary();return value with{Value=negative?-value.Value:value.Value};}
                return Atom();
            }
            finally{depth--;}
        }
        private ParameterQuantity Atom()
        {
            if(Token.Kind==Kind.Number)
            {
                var value=new ParameterQuantity(Token.Number);Next();
                // Only recognized unit suffixes are implicit multiplication; "2 width" is rejected.
                if(Token.Kind==Kind.Name&&Constants.TryGetValue(Token.Text,out var unit)&&!Token.Text.Equals("pi",StringComparison.OrdinalIgnoreCase))
                {Next();return Check(unit with{Value=value.Value*unit.Value});}
                return value;
            }
            if(Token.Kind==Kind.Open){Next();var value=Sum();Require(Kind.Close);return value;}
            if(Token.Kind!=Kind.Name)throw Error("Expected number, parameter or parenthesis");
            string name=Token.Text;Next();
            if(Token.Kind!=Kind.Open)
            {
                if(Functions.Contains(name,StringComparer.OrdinalIgnoreCase))throw Error($"Function {name} requires arguments");
                return Check(Constants.TryGetValue(name,out var constant)?constant:lookup(name));
            }
            if(!Functions.Contains(name,StringComparer.OrdinalIgnoreCase))throw Error($"Unknown function {name}");
            Next();var a=Sum();ParameterQuantity? b=null;
            if(Token.Kind==Kind.Comma){Next();b=Sum();}Require(Kind.Close);
            switch(name.ToLowerInvariant())
            {
                case "min":case "max":
                    if(b is not {} right)throw Error("min/max require two arguments");Same(a,right);
                    return a with{Value=name.Equals("min",StringComparison.OrdinalIgnoreCase)?Math.Min(a.Value,right.Value):Math.Max(a.Value,right.Value)};
                case "abs":if(b is not null)throw Error("abs requires one argument");return a with{Value=Math.Abs(a.Value)};
                case "sqrt":
                    if(b is not null||a.Value<0||a.LengthPower%2!=0||a.AnglePower%2!=0)throw Error("Invalid square root dimension or value");
                    return Check(new(Math.Sqrt(a.Value),a.LengthPower/2,a.AnglePower/2));
                default:
                    if(b is not null||!a.HasDimension(ParameterDimension.Angle))throw Error("Trigonometry requires one angle argument");
                    return Check(new(name.ToLowerInvariant() switch{"sin"=>Math.Sin(a.Value),"cos"=>Math.Cos(a.Value),_=>Math.Tan(a.Value)}));
            }
        }
        private void Require(Kind kind){if(Token.Kind!=kind)throw Error($"Expected {kind}");Next();}
        private void Same(ParameterQuantity a,ParameterQuantity b)
        {if(a.LengthPower!=b.LengthPower||a.AnglePower!=b.AnglePower)throw Error("Incompatible dimensions");}
        private ParameterQuantity Check(ParameterQuantity value)
        {
            if(!double.IsFinite(value.Value)||Math.Abs(value.Value)>1e12||Math.Abs(value.LengthPower)>8||Math.Abs(value.AnglePower)>8)
                throw Error("Expression value or dimension exceeds limits");
            return value;
        }
        private CadValidationException Error(string message)=>new($"{message} at character {position}.");
    }
}
