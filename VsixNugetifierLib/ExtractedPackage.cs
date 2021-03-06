﻿namespace VsixNugetifierLib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Xml.Linq;

    public class ExtractedPackage
    {
        private readonly string extractPath;
        private readonly List<ExtractedPart> extractedParts = new List<ExtractedPart>();

        public ExtractedPackage(string extractPath, IEnumerable<ExtractedPart> extractedParts)
        {
            this.extractPath = extractPath;
            this.extractedParts.AddRange(extractedParts);
        }

        public string ExtractPath
        {
            get { return this.extractPath; }
        }

        public IList<ExtractedPart> ExtractedParts
        {
            get { return this.extractedParts; }
        }

        private IEnumerable<ExtractedPart> ExtractedPackages
        {
            get { return this.ExtractedParts.Where(p => p.Package != null); }
        }


        public IList<ExtractedPackage> Packages
        {
            get { return this.extractedParts.Where(p => p.Package != null).Select(p => p.Package).ToList(); }
        }

        public void AddPackages(string packagePath)
        {
            var packageTargetFolder = Path.Combine(this.ExtractPath, "packages");

            foreach (var nupkg in Directory.EnumerateFiles(packagePath, "*.nupkg", SearchOption.AllDirectories))
            {
                var targetFileName = Path.Combine(packageTargetFolder, Path.GetFileName(nupkg));
                EnsureDirectoryExists(targetFileName);
                File.Copy(nupkg, targetFileName);
            }
        }

        public void UpdateViews(Manifest manifest)
        {
            foreach (var part in this.ExtractedPackages)
            {
                var extractPath = part.Package.ExtractPath;
                var viewsFolder = Path.Combine(extractPath, "views");

                if (!Directory.Exists(viewsFolder))
                {
                    return;
                }

                var templateFileName = Path.Combine(extractPath, manifest.ProjectName + ".vstemplate");
                var template = XDocument.Load(templateFileName);

                var modelText = manifest.ProjectName;

                bool foundView = false;

                var files = Directory.EnumerateFiles(viewsFolder, "*.cshtml", SearchOption.AllDirectories);
                files = files.Union(Directory.EnumerateFiles(extractPath, "*.cs", SearchOption.AllDirectories));
                files = files.Union(Directory.EnumerateFiles(extractPath, "*.asax", SearchOption.TopDirectoryOnly));

                foreach (var viewFileName in files)
                {
                    var fileText = File.ReadAllText(viewFileName);
                    if (fileText.Contains(modelText))
                    {
                        fileText = fileText.Replace(modelText, "$safeprojectname$");
                        File.WriteAllText(viewFileName, fileText);

                        template.Root.Descendants()
                            .Where(d => !string.IsNullOrEmpty(d.Value))
                            .Single(d => viewFileName.EndsWith(d.Value, StringComparison.OrdinalIgnoreCase))
                            .SetAttributeValue("ReplaceParameters", "true");

                        foundView = true;
                    }
                }

                if (foundView)
                {
                    template.Save(templateFileName);
                }
            }
        }

        public void UpdateContentTypes()
        {
            var contentTypesFileName = Path.Combine(this.ExtractPath, "[Content_Types].xml");
            var contentTypes = XDocument.Load(contentTypesFileName);

            var ns = contentTypes.Root.GetDefaultNamespace();
            contentTypes.Root.Add(
                new XElement(ns + "Default",
                    new XAttribute("ContentType", "application/zip"),
                    new XAttribute("Extension", "nupkg")));

            contentTypes.Save(contentTypesFileName);
        }

        public Manifest UpdateManifest(string packagePath)
        {
            var manifestFileName = Path.Combine(this.ExtractPath, "extension.vsixmanifest");

            var document = XDocument.Load(manifestFileName);
            XNamespace ns = document.Root.GetDefaultNamespace();
            var content = document.Descendants(ns + "Content").Single();

            var identifier = document.Root.Elements(ns + "Identifier").Single();
            var packageId = identifier.Attribute("Id").Value;

            var projectName = document.Root.Element(ns + "Content").Element(ns + "ProjectTemplate").Value;

            var references = document.Root.Elements(ns + "References").SingleOrDefault();
            if (references == null)
            {
                throw new InvalidOperationException();
            }

            var nuPackReference = references
                .Elements(ns + "Reference")
                .Where(r => r.Attribute("Id").Value == "NuPackToolsVsix.Microsoft.67e54e40-0ae3-42c5-a949-fddf5739e7a5")
                .SingleOrDefault();

            if (nuPackReference == null)
            {
                references.Add(
                    new XElement(
                        ns + "Reference",
                        new XAttribute("Id", "NuPackToolsVsix.Microsoft.67e54e40-0ae3-42c5-a949-fddf5739e7a5"),
                        new XAttribute("MinVersion", "1.5.20902.9026"),
                        new XElement(ns + "Name", "NuGet Package Manager"),
                        new XElement(ns + "MoreInfoUrl", "http://docs.nuget.org/")));
            }

            foreach (var nupkg in Directory.EnumerateFiles(packagePath, "*.nupkg", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(nupkg);
                content.Add(new XElement(ns + "CustomExtension", new XAttribute("Type", name), "packages/" + name));
            }

            document.Save(manifestFileName);

            return new Manifest(packageId, projectName);
        }

        public void RepackageTemplates(string packageId, string solutionPath)
        {
            foreach (var part in this.ExtractedPackages)
            {
                part.Package.UpdateTemplate(packageId, solutionPath, part.Part.UriString);

                var newTemplate = PackageWrapper.CreateNew(part.ExtractedFileName);
                newTemplate.AddDirectoryAndSave(part.Package.ExtractPath);
            }
        }

        public void UpdateTemplate(string packageId, string solutionPath, string partUriString)
        {
            var vstemplate = Directory.EnumerateFiles(this.ExtractPath, "*.vstemplate").Single();
            var document = XDocument.Load(vstemplate);
            var ns = document.Root.GetDefaultNamespace();

            var wizardExtension = document.Root.Element(ns + "WizardExtension");
            if (wizardExtension == null)
            {
                document.Root.Add(
                    new XElement(ns + "WizardExtension",
                        new XElement(ns + "Assembly", "NuGet.VisualStudio.Interop, Version=1.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"),
                        new XElement(ns + "FullClassName", "NuGet.VisualStudio.TemplateWizard")));
            }

            var wizardData = document.Root.Element(ns + "WizardData");

            if (wizardData == null)
            {
                wizardData = new XElement(ns + "WizardData");
                document.Root.Add(wizardData);

                var packagesElement = new XElement(ns + "packages");
                wizardData.Add(packagesElement);

                var projectName = Path.GetDirectoryName(partUriString);
                var projectFolder = Path.Combine(solutionPath, projectName);
                var packagesConfigFileName = Path.Combine(projectFolder, "packages.config");

                var packagesConfig = XDocument.Load(packagesConfigFileName);
                foreach (var package in packagesConfig.Root.Elements("package"))
                {
                    var id = package.Attribute("id").Value;
                    var version = package.Attribute("version").Value;

                    packagesElement.Add(
                        new XElement(ns + "package",
                            new XAttribute("id", id),
                            new XAttribute("version", version)));
                }
            }

            var packages = wizardData.Element(ns + "packages");
            packages.SetAttributeValue("repository", "extension");
            packages.SetAttributeValue("repositoryId", packageId);

            document.Save(vstemplate);
        }

        private static void EnsureDirectoryExists(string fileName)
        {
            var folder = Path.GetDirectoryName(fileName);

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
        }
    }
}
