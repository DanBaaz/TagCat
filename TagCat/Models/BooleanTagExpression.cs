using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaTagger.Models
{
    /// <summary>
    /// A boolean expression over tag names - AND, OR, NOT, parentheses - compiled once from
    /// typed text and then evaluated per file. Exists because the plain checklist can only
    /// express "all of these" or "any of these"; this covers arbitrary nesting like
    /// "beach AND (sunset OR sunrise) AND NOT blurry", which the checklist has no way to say.
    ///
    /// Deliberately requires explicit AND/OR/NOT rather than inferring an operator from two
    /// adjacent tag names - fewer ways to misread what an ambiguous expression was supposed
    /// to mean.
    /// </summary>
    public abstract class BooleanTagExpression
    {
        public abstract bool Evaluate(IReadOnlyCollection<string> tags);

        /// <summary>Every tag name written into the expression, so the caller can check them
        /// against what actually exists and flag a typo as an error rather than letting it
        /// silently match nothing.</summary>
        public abstract IEnumerable<string> ReferencedTags();

        /// <summary>Throws FormatException with a message meant to be shown directly to the
        /// person, not just logged - so its wording matters as much as its correctness.</summary>
        public static BooleanTagExpression Parse(string text)
        {
            var tokens = Tokenize(text);
            if (tokens.Count == 0)
                throw new FormatException("Type a tag name to get started.");

            var parser = new Parser(tokens);
            var expression = parser.ParseOr();
            parser.ExpectEnd();
            return expression;
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '(' || c == ')')
                {
                    tokens.Add(c.ToString());
                    i++;
                    continue;
                }

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')') i++;
                tokens.Add(text[start..i]);
            }

            return tokens;
        }

        private static bool IsOperator(string? token, string op) =>
            token != null && string.Equals(token, op, StringComparison.OrdinalIgnoreCase);

        /// <summary>Standard precedence, weakest to strongest: OR, then AND, then NOT.</summary>
        private sealed class Parser
        {
            private readonly List<string> _tokens;
            private int _pos;

            public Parser(List<string> tokens) => _tokens = tokens;

            private string? Peek() => _pos < _tokens.Count ? _tokens[_pos] : null;
            private string Consume() => _tokens[_pos++];

            public void ExpectEnd()
            {
                if (_pos != _tokens.Count)
                    throw new FormatException($"'{Peek()}' doesn't belong there - check the parentheses balance.");
            }

            public BooleanTagExpression ParseOr()
            {
                var left = ParseAnd();
                while (IsOperator(Peek(), "OR"))
                {
                    Consume();
                    left = new OrNode(left, ParseAnd());
                }
                return left;
            }

            private BooleanTagExpression ParseAnd()
            {
                var left = ParseNot();
                while (IsOperator(Peek(), "AND"))
                {
                    Consume();
                    left = new AndNode(left, ParseNot());
                }
                return left;
            }

            private BooleanTagExpression ParseNot()
            {
                if (IsOperator(Peek(), "NOT"))
                {
                    Consume();
                    return new NotNode(ParseNot());
                }
                return ParsePrimary();
            }

            private BooleanTagExpression ParsePrimary()
            {
                var token = Peek() ?? throw new FormatException("The expression stops too soon - something's missing at the end.");

                if (token == "(")
                {
                    Consume();
                    var inner = ParseOr();
                    if (Peek() != ")") throw new FormatException("Missing a closing ')'.");
                    Consume();
                    return inner;
                }

                if (token == ")")
                    throw new FormatException("Found a ')' with nothing before it to close.");

                if (IsOperator(token, "AND") || IsOperator(token, "OR") || IsOperator(token, "NOT"))
                    throw new FormatException($"'{token}' needs a tag name next to it, not another operator.");

                Consume();
                return new TagNode(token);
            }
        }

        private sealed class TagNode : BooleanTagExpression
        {
            private readonly string _tag;
            public TagNode(string tag) => _tag = tag;

            public override bool Evaluate(IReadOnlyCollection<string> tags) =>
                tags.Contains(_tag, StringComparer.OrdinalIgnoreCase);

            public override IEnumerable<string> ReferencedTags() { yield return _tag; }
        }

        private sealed class AndNode : BooleanTagExpression
        {
            private readonly BooleanTagExpression _left, _right;
            public AndNode(BooleanTagExpression left, BooleanTagExpression right) => (_left, _right) = (left, right);

            public override bool Evaluate(IReadOnlyCollection<string> tags) =>
                _left.Evaluate(tags) && _right.Evaluate(tags);

            public override IEnumerable<string> ReferencedTags() =>
                _left.ReferencedTags().Concat(_right.ReferencedTags());
        }

        private sealed class OrNode : BooleanTagExpression
        {
            private readonly BooleanTagExpression _left, _right;
            public OrNode(BooleanTagExpression left, BooleanTagExpression right) => (_left, _right) = (left, right);

            public override bool Evaluate(IReadOnlyCollection<string> tags) =>
                _left.Evaluate(tags) || _right.Evaluate(tags);

            public override IEnumerable<string> ReferencedTags() =>
                _left.ReferencedTags().Concat(_right.ReferencedTags());
        }

        private sealed class NotNode : BooleanTagExpression
        {
            private readonly BooleanTagExpression _inner;
            public NotNode(BooleanTagExpression inner) => _inner = inner;

            public override bool Evaluate(IReadOnlyCollection<string> tags) => !_inner.Evaluate(tags);

            public override IEnumerable<string> ReferencedTags() => _inner.ReferencedTags();
        }
    }
}
