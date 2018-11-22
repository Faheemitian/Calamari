﻿using System;
using System.Collections.Specialized;
using Calamari.Aws.Integration;
using Calamari.Deployment;
using Calamari.Deployment.Conventions;
using Calamari.Hooks;
using Calamari.Integration.FileSystem;
using Calamari.Integration.Substitutions;
using Octostache;

namespace Calamari.Terraform
{
    public abstract class TerraformConvention : IInstallConvention
    {
        protected readonly ICalamariFileSystem fileSystem;
        private readonly IFileSubstituter fileSubstituter;
        const string DefaultTerraformFileSubstitution = "**/*.tf\n**/*.tf.json\n**/*.tfvars\n**/*.tfvars.json";

        public TerraformConvention(ICalamariFileSystem fileSystem,  IFileSubstituter fileSubstituter)
        {
            this.fileSystem = fileSystem;
            this.fileSubstituter = fileSubstituter;
        }

        protected abstract void Execute(RunningDeployment deployment, StringDictionary environmentVariables);

        public void Install(RunningDeployment deployment)
        {
            var variables = deployment.Variables;
            var additionalFileSubstitution = variables.Get(TerraformSpecialVariables.Action.Terraform.FileSubstitution);

            variables.Add(SpecialVariables.Package.EnableNoMatchWarning,
                (!String.IsNullOrEmpty(additionalFileSubstitution)).ToString());

            var substitutionPatterns = (DefaultTerraformFileSubstitution +
                                        (string.IsNullOrWhiteSpace(additionalFileSubstitution)
                                            ? String.Empty
                                            : "\n" + additionalFileSubstitution))
                .Split(new[] {"\r", "\n"}, StringSplitOptions.RemoveEmptyEntries);

            new SubstituteInFilesConvention(fileSystem, fileSubstituter,
                    _ => true,
                    _ => substitutionPatterns)
                .Install(deployment);

            var environmentVariables = new StringDictionary();

            var oldAccountStyle = variables.Get(TerraformSpecialVariables.Action.Terraform.ManagedAccountDeprecatedUsedOnlyForAWS);
            var managedAccounts = variables.GetStrings(TerraformSpecialVariables.Action.Terraform.ManagedAccounts, ',');

            if (oldAccountStyle != null)
            {
                if (managedAccounts.Contains(oldAccountStyle))
                {
                    managedAccounts.Add(oldAccountStyle);
                }
            }

            foreach (var managedAccount in managedAccounts)
            {
                switch (managedAccount)
                {
                    case "AWS":
                        var awsEnvironmentGeneration = new AwsEnvironmentGeneration(variables);
                        environmentVariables = environmentVariables.MergeDictionaries(awsEnvironmentGeneration.EnvironmentVars);
                        break;
                    case "Azure":
                        environmentVariables = environmentVariables.MergeDictionaries(AzureEnvironmentVariables(variables));
                        break;
                }
            }

            Execute(deployment, environmentVariables);
        }

        // We not referencing the Azure project because we want to be multi-platform 
        static StringDictionary AzureEnvironmentVariables(VariableDictionary variables)
        {
            string AzureEnvironment(string s)
            {
                switch (s)
                {
                    case "AzureChinaCloud":
                        return "china";
                    case "AzureGermanCloud":
                        return "german";
                    case "AzureUSGovernment":
                        return "usgovernment";
                    default:
                        return "public";
                }
            }

            var environmentName = AzureEnvironment(variables.Get(SpecialVariables.Action.Azure.Environment));

            var env = new StringDictionary
            {
                {"ARM_SUBSCRIPTION_ID", variables.Get(SpecialVariables.Action.Azure.SubscriptionId)},
                {"ARM_CLIENT_ID", variables.Get(SpecialVariables.Action.Azure.ClientId)},
                {"ARM_CLIENT_SECRET", variables.Get(SpecialVariables.Action.Azure.TenantId)},
                {"ARM_TENANT_ID", variables.Get(SpecialVariables.Action.Azure.Password)},
                {"ARM_ENVIRONMENT", environmentName}
            };

            return env;
        }
    }
}