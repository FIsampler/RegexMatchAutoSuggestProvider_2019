﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RegexMASProviderLib.Common;

namespace RegexMASProviderLib.Models
{
    public class RegexPatternEntries : ModelBase
    {

        private BindingList<RegexPatternEntry> _entries;

        public BindingList<RegexPatternEntry> Entries
        {
            get { return _entries; }
            set
            {
                _entries = value;
                OnPropertyChanged("Entries");
            }
        }

        public RegexPatternEntries()
        {
            Entries = new BindingList<RegexPatternEntry>();
        }

        public void Load(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }
            var doc = XDocument.Load(path);
            foreach (var entry in doc.Descendants("Entry"))
            {
                var isEnabledElem = entry.Element("IsEnabled");
                var descElem = entry.Element("Description");
                var regexPatternElem = entry.Element("RegexPattern");
                var replacePatternElem = entry.Element("ReplacePattern");

                Entries.Add(new RegexPatternEntry
                {
                    IsEnabled = isEnabledElem != null ? bool.Parse(isEnabledElem.Value) : true,
                    Description = descElem != null ? descElem.Value : string.Empty,
                    RegexPattern = regexPatternElem != null ? regexPatternElem.Value : string.Empty,
                    ReplacePattern = replacePatternElem != null ? replacePatternElem.Value : string.Empty
                });
            }
        }

        public void Save(string path)
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "true"),
                new XElement("RegexMatchAutoSuggestProvider",
                    new XElement("Settings",
                        new XElement("RegexPatternEntries",
                            Entries.Select(e =>
                                new XElement("Entry",
                                    new XElement("IsEnabled", e.IsEnabled),
                                    new XElement("Description", e.Description),
                                    new XElement("RegexPattern", e.RegexPattern),
                                    new XElement("ReplacePattern", e.ReplacePattern)))
                            ))));
            doc.Save(path);
        }

        public override string Error
        {
            get { return null; }
        }

        [Obsolete("This method is obsolete. Use GetAutoSuggestEntries method, which handles multiple variables correctly.")]
        public List<string> EvaluateMatches(string text, Variables variables)
        {
            var results = new List<string>();
            if (!string.IsNullOrEmpty(text))
            {
                var validVariables = variables.ToDistinct();
                foreach (var pattern in Entries.Where(e => e.IsEnabled && !e.HasErrors))
                {
                    var pairPatterns = GetTranslationPairPatterns(validVariables, pattern.RegexPattern);
                    foreach (var pairPattern in pairPatterns)
                    {
                        var findWord = pairPattern.FindWord;
                        var replaceWord = pairPattern.ReplaceWord;
                        var newtext = Regex.Replace(text, pairPattern.OriginalPattern,
                            match => match.Value.Replace(findWord, replaceWord));
                        results.AddRange(from Match match in Regex.Matches(newtext, pairPattern.ModifiedPattern)
                                         select Utils.WideToNarrow(match.Result(pattern.ReplacePattern)));
                    }

                    results.AddRange(from Match match in Regex.Matches(text, pattern.RegexPattern)
                        select Utils.WideToNarrow(match.Result(pattern.ReplacePattern)));
                }
            }
            return results.Select(e => e).Distinct().ToList();
        }


        /// <summary>
        /// Evaluates regex patterns and variables and return AutoSuggest entries. 
        /// </summary>
        /// <param name="text">Source text</param>
        /// <param name="variables">Variables</param>
        /// <returns>AutoSuggest entries</returns>
        public List<AutoSuggestEntry> GetAutoSuggestEntries(string text, Variables variables)
        {
            var results = new List<string>();
            var autoSuggestEntries = new List<AutoSuggestEntry>();
            if (!string.IsNullOrEmpty(text))
            {
                var validVariables = variables.ToDistinct();
                foreach (var pattern in Entries.Where(e => e.IsEnabled && !e.HasErrors))
                {
                    var intermediateRegex = ConstructIntermediateRegex(validVariables, pattern.RegexPattern);
                    var r = new Regex(intermediateRegex.ConcatenatedFindPattern);
                    //foreach (Match initialMatch in Regex.Matches(text, intermediateRegex.ConcatenatedFindPattern, RegexOptions.IgnoreCase))
                    foreach (Match initialMatch in r.Matches(text))
                    {
                        var finalRegex = ConstructFinalRegex(r, initialMatch, intermediateRegex);
                        foreach (Match finalMatch in Regex.Matches(finalRegex.NewSourceText, finalRegex.EvaluatedFindPattern))
                        {
                            var autoSuggestString = finalMatch.Result(pattern.ReplacePattern).WideToNarrow();
                            results.Add(autoSuggestString);
                            var autoSuggestEntry = new AutoSuggestEntry
                            {
                                OriginalSourceText = text,
                                OriginalFindPattern = pattern.RegexPattern,
                                OriginalReplacePattern = pattern.ReplacePattern,
                                NumberedFindPattern = intermediateRegex.NumberedFindPattern,
                                ConcatenatedFindPattern = intermediateRegex.ConcatenatedFindPattern,
                                ConcatenatedFindPatternMatch = initialMatch,
                                EvaluatedFindPattern = finalRegex.EvaluatedFindPattern,
                                EvaluatedFindPatternMatch = finalMatch,
                                NewSourceText = finalRegex.NewSourceText,
                                AutoSuggestString = autoSuggestString,
                            };
                            autoSuggestEntries.Add(autoSuggestEntry);
                        }
                    }

                    //results.AddRange(from Match match in Regex.Matches(text, pattern.RegexPattern)
                    //                     select match.Result(pattern.ReplacePattern).WideToNarrow());

                }
            }
            //return results.Select(e => e).Distinct().ToList();
            return autoSuggestEntries;
        }

        private class FinalRegex
        {
            public string EvaluatedFindPattern { get; set; }
            public string NewSourceText { get; set; }
        }

        private FinalRegex  ConstructFinalRegex(Regex sourceRegex, Match match, IntermediateRegex intermediateRegex)
        {
            var entireValue = match.Value;
            var finalMatchPattern = intermediateRegex.NumberedFindPattern;
            var groupNames = sourceRegex.GetGroupNames();
            var groups = match.Groups;
            var diff = 0;
            for (int i = 1; i < groupNames.Length; i++)
            {
                var groupName = groupNames[i];
                var group = groups[groupName];
                if (groupName.IsNumber())
                {
                    continue;
                }
                if (!group.Success || !intermediateRegex.VariableMap.ContainsKey(groupName))
                {
                    continue;
                }

                var variable = intermediateRegex.VariableMap[groupName];
                var pair = variable.TranslationPairs.FirstOrDefault(x =>
                    !x.HasErrors && x.Source == group.Value);
                if (pair == null)
                {
                    continue;
                }
                finalMatchPattern = finalMatchPattern.ReplaceFirst($"#{groupName}#", Regex.Escape(pair.Target));

                var beforeLen = entireValue.Length;
                entireValue = entireValue.Remove(group.Index - match.Index - diff, group.Length);
                entireValue = entireValue.Insert(group.Index - match.Index - diff, $"{pair.Target}");
                var afterLen = entireValue.Length;
                diff += beforeLen - afterLen;
            }


            return new FinalRegex
            {
                EvaluatedFindPattern = finalMatchPattern,
                NewSourceText = entireValue
            };

        }

        private class IntermediateRegex
        {
            public string ConcatenatedFindPattern { get; set; }

            /// <summary>
            /// Used to construct a final regex pattern.
            /// </summary>
            public string NumberedFindPattern { get; set; }

            /// <summary>
            /// Name to Variable Mapping. Name is suffixed with a unique index.
            /// </summary>
            public Dictionary<string, Variable> VariableMap { get; set; }
        }


        private IntermediateRegex ConstructIntermediateRegex(IEnumerable< Variable> variables, string basedPattern)
        {
            var intermediateRegex = new IntermediateRegex{
                ConcatenatedFindPattern = basedPattern,
                NumberedFindPattern = basedPattern,
                VariableMap = new Dictionary<string, Variable>()
            };

            foreach (var variable in variables.Where(p => !p.HasErrors && p.IsEnabled))
            {

                var idx = 0;
                while (intermediateRegex.ConcatenatedFindPattern.Contains($"#{variable.Name}#"))
                {
                    var groupName = $"{variable.Name}{idx}";
                    var concatenatedSources = string.Join("|", variable.TranslationPairs.Where(p => !p.HasErrors).Select(p => Regex.Escape(p.Source)));
                    intermediateRegex.ConcatenatedFindPattern = intermediateRegex.ConcatenatedFindPattern.ReplaceFirst($"#{variable.Name}#", $"(?<{groupName}>{concatenatedSources})");
                    intermediateRegex.NumberedFindPattern =
                        intermediateRegex.NumberedFindPattern.ReplaceFirst($"#{variable.Name}#", $"#{groupName}#");
                    intermediateRegex.VariableMap.Add(groupName, variable);
                    idx++;
                }
            }

            return intermediateRegex;
        }

        private List<TranslationPairPattern> GetTranslationPairPatterns(List<Variable> variables, string regexPattern)
        {
            var list = new List<TranslationPairPattern>();
            foreach (var variable in variables)
            {
                var varname = "#" + variable.Name + "#";
                if (!regexPattern.Contains(varname))
                {
                    continue;
                }
                foreach (var pair in variable.TranslationPairs.Where(p => !p.HasErrors))
                {
                    var findPattern = regexPattern.Replace(varname,
                        string.Format("(?:{0})", Regex.Escape(pair.Source)));
                    var replacePattern = regexPattern.Replace(varname,
                        string.Format("(?:{0})", Regex.Escape(pair.Target)));
                    list.Add(new TranslationPairPattern
                    {
                        OriginalPattern = findPattern,
                        FindWord = pair.Source,
                        ModifiedPattern = replacePattern,
                        ReplaceWord = pair.Target
                    });
                }
            }
            return list;
        }

        private class TranslationPairPattern
        {
            public string OriginalPattern { get; set; }
            public string FindWord { get; set; }
            public string ModifiedPattern { get; set; }
            public string ReplaceWord { get; set; }
        }
    }
}