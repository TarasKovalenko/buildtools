// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text;

namespace Microsoft.DotNet.Build.Tasks
{
    public class GenerateResourcesCode : BuildTask
    {
        private TargetLanguage _targetLanguage = TargetLanguage.CSharp;
        private StreamWriter _targetStream;
        private string _resourcesName;
        private string _srClassName;
        private string _srNamespace;

        [Required]
        public string ResxFilePath { get; set; }

        [Required]
        public string OutputSourceFilePath { get; set; }

        [Required]
        public string AssemblyName { get; set; }

        /// <summary>
        /// Defines the namespace in which the generated SR class is defined.  If not specified defaults to System.
        /// </summary>
        public string SRNamespace { get; set; }

        /// <summary>
        /// Defines the class in which the ResourceType property is generated.  If not specified defaults to SR.
        /// </summary>
        public string SRClassName { get; set; }


        /// <summary>
        /// Defines the namespace in which the generated resources class is defined.  If not specified defaults to SRNamespace.
        /// </summary>
        public string ResourcesNamespace { get; set; }

        /// <summary>
        /// Defines the class in which the resource properties/constants are generated.  If not specified defaults to SRClassName.
        /// </summary>
        public string ResourcesClassName { get; set; }

        /// <summary>
        /// Emit constant key strings instead of properties that retrieve values.
        /// </summary>
        public bool AsConstants { get; set; }

        public override bool Execute()
        {
            try
            {
                _resourcesName = "FxResources." + AssemblyName;
                _srNamespace = String.IsNullOrEmpty(SRNamespace) ? "System" : SRNamespace;
                _srClassName = String.IsNullOrEmpty(SRClassName) ? "SR" : SRClassName;

                using (_targetStream = File.CreateText(OutputSourceFilePath))
                {
                    if (String.Equals(Path.GetExtension(OutputSourceFilePath), ".vb", StringComparison.OrdinalIgnoreCase))
                    {
                        _targetLanguage = TargetLanguage.VB;
                    }
                    WriteSR();
                    WriteResources();
                }
            }
            catch (Exception e)
            {
                Log.LogError("Failed to generate the resource code with error:\n" + e.Message);
                return false; // fail the task
            }

            return true;
        }

        private void WriteSR()
        {
            string commentPrefix = _targetLanguage == TargetLanguage.CSharp ? "// " : "' ";
            _targetStream.WriteLine(commentPrefix + "Do not edit this file manually it is auto-generated during the build based on the .resx file for this project.");


            if (_targetLanguage == TargetLanguage.CSharp)
            {
                _targetStream.WriteLine($"namespace {_srNamespace}");
                _targetStream.WriteLine("{");
                _targetStream.WriteLine("");
                _targetStream.WriteLine($"    internal static partial class {_srClassName}");
                _targetStream.WriteLine("    {");
                _targetStream.WriteLine($"        internal static Type ResourceType {{ get; }} = typeof({_resourcesName}.SR); ");
                _targetStream.WriteLine("    }");
                _targetStream.WriteLine("}");
                _targetStream.WriteLine("");
                _targetStream.WriteLine($"namespace {_resourcesName}");
                _targetStream.WriteLine("{");
                _targetStream.WriteLine("    // The type of this class is used to create the ResourceManager instance as the type name matches the name of the embedded resources file");
                _targetStream.WriteLine("    internal static class SR");
                _targetStream.WriteLine("    {");
                _targetStream.WriteLine("    }");
                _targetStream.WriteLine("}");
                _targetStream.WriteLine("");
            }
            else
            {
                _targetStream.WriteLine($"Namespace {_srNamespace}");
                _targetStream.WriteLine("");
                _targetStream.WriteLine($"    Friend Partial Class {_srClassName}");
                _targetStream.WriteLine($"        Friend Shared ReadOnly Property ResourceType As Type = GetType({_resourcesName}.SR)");
                _targetStream.WriteLine("    End Class");
                _targetStream.WriteLine("End Namespace");
                _targetStream.WriteLine("");
                _targetStream.WriteLine($"Namespace {_resourcesName}");
                _targetStream.WriteLine("    ' The type of this class is used to create the ResourceManager instance as the type name matches the name of the embedded resources file");
                _targetStream.WriteLine("    Friend Class SR");
                _targetStream.WriteLine("    ");
                _targetStream.WriteLine("    End Class");
                _targetStream.WriteLine("End Namespace");
                _targetStream.WriteLine("");
            }
        }

        private void WriteResources()
        {
            var resources = GetResources(ResxFilePath);

            var accessorNamespace = String.IsNullOrEmpty(ResourcesNamespace) ? _srNamespace : ResourcesNamespace;
            var accessorClassName = String.IsNullOrEmpty(ResourcesClassName) ? _srClassName : ResourcesClassName;

            if (_targetLanguage == TargetLanguage.CSharp)
            {
                _targetStream.WriteLine($"namespace {accessorNamespace}");
                _targetStream.WriteLine("{");
                _targetStream.WriteLine("");
                _targetStream.WriteLine($"    internal static partial class {accessorClassName}");
                _targetStream.WriteLine("    {");
                _targetStream.WriteLine("");

            }
            else
            {
                _targetStream.WriteLine($"Namespace {accessorNamespace}");
                _targetStream.WriteLine("");
                _targetStream.WriteLine($"    Friend Partial Class {accessorClassName}");
                _targetStream.WriteLine("");
            }

            if (AsConstants)
            {
                foreach (var resourcePair in resources)
                {
                    WriteResourceConstant((string)resourcePair.Key);
                }
            }
            else
            {
                _targetStream.WriteLine(_targetLanguage == TargetLanguage.CSharp ?
                    "#if !DEBUGRESOURCES" :
                    "#If Not DEBUGRESOURCES Then");

                foreach (var resourcePair in resources)
                {
                    WriteResourceProperty(resourcePair.Key, _targetLanguage == TargetLanguage.CSharp ?
                        "null" :
                        "Nothing");
                }

                _targetStream.WriteLine(_targetLanguage == TargetLanguage.CSharp ?
                    "#else" :
                    "#Else");

                foreach (var resourcePair in resources)
                {
                    WriteResourceProperty(resourcePair.Key, CreateStringLiteral(resourcePair.Value));
                }

                _targetStream.WriteLine(_targetLanguage == TargetLanguage.CSharp ?
                    "#endif" :
                    "#End If");
            }

            if (_targetLanguage == TargetLanguage.CSharp)
            {
                _targetStream.WriteLine("    }");
                _targetStream.WriteLine("}");

            }
            else
            {
                _targetStream.WriteLine("    End Class");
                _targetStream.WriteLine("End Namespace");
            }
        }

        private string CreateStringLiteral(string original)
        {
            StringBuilder stringLiteral = new StringBuilder(original.Length + 3);
            if (_targetLanguage == TargetLanguage.CSharp)
            {
                stringLiteral.Append('@');
            }
            stringLiteral.Append('\"');
            for (var i = 0; i < original.Length; i++)
            {
                // duplicate '"' for VB and C#
                if (original[i] == '\"')
                {
                    stringLiteral.Append("\"");
                }
                stringLiteral.Append(original[i]);
            }
            stringLiteral.Append('\"');

            return stringLiteral.ToString();
        }

        private void WriteResourceProperty(string resourceId, string resourceValueLiteral)
        {
            if (_targetLanguage == TargetLanguage.CSharp)
            {
                _targetStream.WriteLine($"        internal static string {resourceId} {{");
                _targetStream.WriteLine($"            get {{ return {_srNamespace}.{_srClassName}.GetResourceString(\"{resourceId}\", {resourceValueLiteral}); }}");
                _targetStream.WriteLine($"        }}");
            }
            else
            {
                _targetStream.WriteLine($"        Friend Shared ReadOnly Property {resourceId} As String");
                _targetStream.WriteLine($"           Get");
                _targetStream.WriteLine($"               Return  {_srNamespace}.{_srClassName}.GetResourceString(\"{resourceId}\", {resourceValueLiteral})");
                _targetStream.WriteLine($"           End Get");
                _targetStream.WriteLine($"        End Property");
            }
        }
        private void WriteResourceConstant(string resourceId)
        {
            if (_targetLanguage == TargetLanguage.CSharp)
            {
                _targetStream.WriteLine($"        internal const string {resourceId} = \"{resourceId}\";");
            }
            else
            {
                _targetStream.WriteLine($"        Friend Const {resourceId} As String = \"{resourceId}\"");
            }
        }

        
        private enum TargetLanguage
        {
            CSharp, VB
        }

        internal Dictionary<string, string> GetResources(string fileName)
        {
            Dictionary<string, string> resources = new Dictionary<string, string>();

            XDocument doc = XDocument.Load(fileName, LoadOptions.PreserveWhitespace);
            foreach (XElement dataElem in doc.Element("root").Elements("data"))
            {
                string name = dataElem.Attribute("name").Value;
                string value = dataElem.Element("value").Value;
                if (resources.ContainsKey(name))
                {
                    Log.LogError($"Duplicate resource id \"{name}\"");
                }
                else
                {
                    resources[name] = value;
                }
            }

            return resources;
        }
    }
}
