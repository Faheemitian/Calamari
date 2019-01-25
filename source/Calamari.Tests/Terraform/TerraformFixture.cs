﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Calamari.Commands.Support;
using Calamari.Deployment;
using Calamari.Integration.FileSystem;
using Calamari.Terraform;
using Calamari.Tests.Fixtures;
using Calamari.Tests.Helpers;
using FluentAssertions;
using NUnit.Framework;
using Octostache;
using Newtonsoft.Json.Linq;

namespace Calamari.Tests.Terraform
{
    [TestFixture]
    [RequiresNonFreeBSDPlatform]
    [RequiresNon32BitWindows]
    [RequiresNonMac]
    [RequiresNonMono]
    [Category(TestCategory.CompatibleOS.Windows)]
    public class TerraformFixture
    {
        private string customTerraformExecutable;

        [OneTimeSetUp]
        public async Task InstallTools()
        {
            var destinationDirectoryName = TestEnvironment.GetTestPath("TerraformCLIPath");

            if (Directory.Exists(destinationDirectoryName))
            {
                var path = Directory.EnumerateFiles(destinationDirectoryName).FirstOrDefault();
                if (path != null)
                {
                    customTerraformExecutable = path;
                    Console.WriteLine($"Using existing terraform located in {customTerraformExecutable}");
                    return;
                }
            }

            Console.WriteLine("Downloading terraform cli...");

            using (var client = new HttpClient())
            {
                var json = await client.GetStringAsync("https://checkpoint-api.hashicorp.com/v1/check/terraform");

                var parsedJson = JObject.Parse(json);

                var downloadBaseUrl = parsedJson["current_download_url"].Value<string>();
                var currentVersion = parsedJson["current_version"].Value<string>();
                var fileName = $"terraform_{currentVersion}_windows_amd64.zip";

                if (CalamariEnvironment.IsRunningOnNix)
                {
                    fileName = $"terraform_{currentVersion}_linux_amd64.zip";
                }
                var zipPath = Path.Combine(Path.GetTempPath(), fileName);
                using (new TemporaryFile(zipPath))
                {
                    using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var stream = await client.GetStreamAsync($"{downloadBaseUrl}{fileName}"))
                    {
                        await stream.CopyToAsync(fileStream);
                    }

                    ZipFile.ExtractToDirectory(zipPath, destinationDirectoryName);
                }

                customTerraformExecutable = Directory.EnumerateFiles(destinationDirectoryName).FirstOrDefault();
                Console.WriteLine($"Downloaded terraform to {customTerraformExecutable}");
            }
        }

        [Test]
        public void ExtraInitParametersAreSet()
        {
            ExecuteAndReturnLogOutput<PlanCommand>(_ =>
                    _.Set(TerraformSpecialVariables.Action.Terraform.AdditionalInitParams, "-upgrade"), "Simple")
                .Should().Contain("init -no-color -get-plugins=true -upgrade");
        }

        [Test]
        public void AllowPluginDownloadsShouldBeDisabled()
        {
            ExecuteAndReturnLogOutput<PlanCommand>(_ =>
                    _.Set(TerraformSpecialVariables.Action.Terraform.AllowPluginDownloads, false.ToString()), "Simple")
                .Should().Contain("init -no-color -get-plugins=false");
        }

        [Test]
        public void AttachLogFile()
        {
            ExecuteAndReturnLogOutput<PlanCommand>(_ =>
                    _.Set(TerraformSpecialVariables.Action.Terraform.AttachLogFile, true.ToString()), "Simple")
                .Should().Contain("##octopus[createArtifact ");
        }

        [Test]
        [TestCase(typeof(PlanCommand), "plan -no-color -detailed-exitcode -var my_var=\"Hello world\"")]
        [TestCase(typeof(ApplyCommand), "apply -no-color -auto-approve -var my_var=\"Hello world\"")]
        [TestCase(typeof(DestroyPlanCommand), "plan -no-color -detailed-exitcode -destroy -var my_var=\"Hello world\"")]
        [TestCase(typeof(DestroyCommand), "destroy -force -no-color -var my_var=\"Hello world\"")]
        public void AdditionalActionParams(Type commandType, string expected)
        {
            ExecuteAndReturnLogOutput(commandType, _ =>
                {
                    _.Set(TerraformSpecialVariables.Action.Terraform.AdditionalActionParams, "-var my_var=\"Hello world\"");
                }, "Simple")
                .Should().Contain(expected);
        }

        [Test]
        [TestCase(typeof(PlanCommand), "plan -no-color -detailed-exitcode -var-file=\"example.tfvars\"")]
        [TestCase(typeof(ApplyCommand), "apply -no-color -auto-approve -var-file=\"example.tfvars\"")]
        [TestCase(typeof(DestroyPlanCommand), "plan -no-color -detailed-exitcode -destroy -var-file=\"example.tfvars\"")]
        [TestCase(typeof(DestroyCommand), "destroy -force -no-color -var-file=\"example.tfvars\"")]
        public void VarFiles(Type commandType, string actual)
        {
            ExecuteAndReturnLogOutput(commandType, _ =>
                {
                    _.Set(TerraformSpecialVariables.Action.Terraform.VarFiles, "example.tfvars");
                }, "WithVariables")
                .Should().Contain(actual);
        }

        [Test]
        public void OutputAndSubstituteOctopusVariables()
        {
            ExecuteAndReturnLogOutput<ApplyCommand>(_ =>
                {
                    _.Set(TerraformSpecialVariables.Action.Terraform.VarFiles, "example.txt");
                    _.Set(TerraformSpecialVariables.Action.Terraform.FileSubstitution, "example.txt");
                    _.Set("Octopus.Action.StepName", "Step Name");
                    _.Set("Should_Be_Substituted", "Hello World");
                    _.Set("Should_Be_Substituted_in_txt", "Hello World from text");
                }, "WithVariablesSubstitution")
                .Should()
                .Contain("Octopus.Action[\"Step Name\"].Output.TerraformValueOutputs[\"my_output\"]' with the value only of 'Hello World'")
                .And
                .Contain("Octopus.Action[\"Step Name\"].Output.TerraformValueOutputs[\"my_output_from_txt_file\"]' with the value only of 'Hello World from text'");
        }

        [Test]
        [TestCase(typeof(PlanCommand))]
        [TestCase(typeof(DestroyPlanCommand))]
        public void TerraformPlanOutput(Type commandType)
        {
            ExecuteAndReturnLogOutput(commandType, _ =>
                {
                    _.Set("Octopus.Action.StepName", "Step Name");
                }, "Simple")
                .Should().Contain("Octopus.Action[\"Step Name\"].Output.TerraformPlanOutput");
        }

        [Test]
        public void UsesWorkSpace()
        {
            ExecuteAndReturnLogOutput<ApplyCommand>(_ =>
                {
                    _.Set(TerraformSpecialVariables.Action.Terraform.Workspace, "myspace");
                }, "Simple")
                .Should().Contain("workspace new \"myspace\"");
        }

        [Test]
        public void UsesTemplateDirectory()
        {
            ExecuteAndReturnLogOutput<ApplyCommand>(_ =>
                {
                    _.Set(TerraformSpecialVariables.Action.Terraform.TemplateDirectory, "SubFolder");
                }, "TemplateDirectory")
                .Should().Contain("SubFolder\\example.tf");
        }

        [Test]
        public async Task AzureIntegration()
        {
            var bucketName = $"cfe2e-{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            void PopulateVariables(VariableDictionary _)
            {
                _.Set(TerraformSpecialVariables.Action.Terraform.FileSubstitution, "test.txt");
                _.Set(SpecialVariables.Action.Azure.SubscriptionId, Environment.GetEnvironmentVariable("Azure_OctopusAPITester_SubscriptionId"));
                _.Set(SpecialVariables.Action.Azure.TenantId, Environment.GetEnvironmentVariable("Azure_OctopusAPITester_TenantId"));
                _.Set(SpecialVariables.Action.Azure.ClientId, Environment.GetEnvironmentVariable("Azure_OctopusAPITester_ClientId"));
                _.Set(SpecialVariables.Action.Azure.Password, Environment.GetEnvironmentVariable("Azure_OctopusAPITester_Password"));
                _.Set("Hello", "Hello World from Azure");
                _.Set("bucket_name", bucketName);
                _.Set(TerraformSpecialVariables.Action.Terraform.VarFiles, "example.tfvars");
                _.Set(TerraformSpecialVariables.Action.Terraform.AzureManagedAccount, true.ToString());
            }

            ExecuteAndReturnLogOutput<PlanCommand>(PopulateVariables, "Azure")
                .Should().Contain("Octopus.Action[\"\"].Output.TerraformPlanOutput");

            ExecuteAndReturnLogOutput<ApplyCommand>(PopulateVariables, "Azure")
                .Should().Contain($"Saving variable 'Octopus.Action[\"\"].Output.TerraformValueOutputs[\"url\"]' with the value only of 'http://terraformtestaccount.blob.core.windows.net/{bucketName}/test.txt'");

            string fileData;
            using (var client = new HttpClient())
            {
                fileData = await client.GetStringAsync($"http://terraformtestaccount.blob.core.windows.net/{bucketName}/test.txt").ConfigureAwait(false);
            }

            fileData.Should().Be("Hello World from Azure");

            ExecuteAndReturnLogOutput<DestroyCommand>(PopulateVariables, "Azure")
                .Should().Contain("destroy -force -no-color");
        }

        [Test]
        public async Task AWSIntegration()
        {
            var bucketName = $"cfe2e-{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            void PopulateVariables(VariableDictionary _)
            {
                _.Set(TerraformSpecialVariables.Action.Terraform.FileSubstitution, "test.txt");
                _.Set("Octopus.Action.Amazon.AccessKey", Environment.GetEnvironmentVariable("AWS.E2E.AccessKeyId"));
                _.Set("Octopus.Action.Amazon.SecretKey", Environment.GetEnvironmentVariable("AWS.E2E.SecretKeyId"));
                _.Set("Octopus.Action.Aws.Region", "ap-southeast-1");
                _.Set("Hello", "Hello World from AWS");
                _.Set("bucket_name", bucketName);
                _.Set(TerraformSpecialVariables.Action.Terraform.VarFiles, "example.tfvars");
                _.Set(TerraformSpecialVariables.Action.Terraform.AWSManagedAccount, "AWS");
            }

            using (var outputs = ExecuteAndReturnLogOutput(PopulateVariables, "AWS", typeof(PlanCommand), typeof(ApplyCommand), typeof(DestroyCommand)).GetEnumerator())
            {
                outputs.MoveNext();
                outputs.Current.Should()
                    .Contain("Octopus.Action[\"\"].Output.TerraformPlanOutput");

                outputs.MoveNext();
                outputs.Current.Should()
                    .Contain($"Saving variable 'Octopus.Action[\"\"].Output.TerraformValueOutputs[\"url\"]' with the value only of 'https://{bucketName}.s3.amazonaws.com/test.txt'");

                string fileData;
                using (var client = new HttpClient())
                {
                    fileData = await client.GetStringAsync($"https://{bucketName}.s3.amazonaws.com/test.txt").ConfigureAwait(false);
                }

                fileData.Should().Be("Hello World from AWS");

                outputs.MoveNext();
                outputs.Current.Should()
                    .Contain("destroy -force -no-color");
            }
        }


        [Test]
        public void PlanDetailedExitCode()
        {
            using (var outputs = ExecuteAndReturnLogOutput(_ => { }, "PlanDetailedExitCode", typeof(PlanCommand), typeof(ApplyCommand), typeof(PlanCommand)).GetEnumerator())
            {
                outputs.MoveNext();
                outputs.Current.Should()
                    .Contain("Saving variable 'Octopus.Action[\"\"].Output.TerraformPlanDetailedExitCode' with the detailed exit code of the plan, with value '2'");

                outputs.MoveNext();
                outputs.Current.Should()
                    .Contain("apply -no-color -auto-approve");

                outputs.MoveNext();
                outputs.Current.Should()
                    .Contain("Saving variable 'Octopus.Action[\"\"].Output.TerraformPlanDetailedExitCode' with the detailed exit code of the plan, with value '0'");
            }
        }

        string ExecuteAndReturnLogOutput(Type commandType, Action<VariableDictionary> populateVariables, string folderName)
        {
            return ExecuteAndReturnLogOutput(populateVariables, folderName, commandType).Single();
        }

        IEnumerable<string> ExecuteAndReturnLogOutput(Action<VariableDictionary> populateVariables, string folderName, params Type[] commandTypes)
        {
            void Copy(string sourcePath, string destinationPath)
            {
                foreach (var dirPath in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
                {
                    Directory.CreateDirectory(dirPath.Replace(sourcePath, destinationPath));
                }

                foreach (var newPath in Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories))
                {
                    File.Copy(newPath, newPath.Replace(sourcePath, destinationPath), true);
                }
            }

            using (var currentDirectory = TemporaryDirectory.Create())
            {
                var variablesFile = Path.GetTempFileName();
                var variables = new VariableDictionary();
                variables.Set(TerraformSpecialVariables.Calamari.TerraformCliPath, Path.GetDirectoryName(customTerraformExecutable));
                variables.Set(SpecialVariables.OriginalPackageDirectoryPath, currentDirectory.DirectoryPath);
                variables.Set(TerraformSpecialVariables.Action.Terraform.CustomTerraformExecutable, customTerraformExecutable);

                populateVariables(variables);

                var terraformFiles = TestEnvironment.GetTestPath("Terraform", folderName);

                Copy(terraformFiles, currentDirectory.DirectoryPath);

                variables.Save(variablesFile);

                using (new TemporaryFile(variablesFile))
                {
                    foreach (var commandType in commandTypes)
                    {
                        var sb = new StringBuilder();
                        Log.StdOut = new IndentedTextWriter(new StringWriter(sb));

                        var command = (ICommand) Activator.CreateInstance(commandType);
                        var result = command.Execute(new[] {"--variables", $"{variablesFile}"});

                        result.Should().Be(0);

                        var output = sb.ToString();

                        Console.WriteLine(output);

                        yield return output;
                    }
                }
            }
        }

        string ExecuteAndReturnLogOutput<T>(Action<VariableDictionary> populateVariables, string folderName) where T : ICommand, new()
        {
            return ExecuteAndReturnLogOutput(typeof(T), populateVariables, folderName);
        }
    }
}
