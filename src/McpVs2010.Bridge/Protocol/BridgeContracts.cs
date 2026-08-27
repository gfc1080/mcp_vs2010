using System.Collections.Generic;
using System.Runtime.Serialization;

namespace McpVs2010.Bridge.Protocol
{
    [DataContract]
    internal sealed class BridgeRequest
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "method")]
        public string Method { get; set; }

        [DataMember(Name = "scope", EmitDefaultValue = false)]
        public string Scope { get; set; }

        [DataMember(Name = "operation", EmitDefaultValue = false)]
        public string Operation { get; set; }

        [DataMember(Name = "project", EmitDefaultValue = false)]
        public string Project { get; set; }

        [DataMember(Name = "configuration", EmitDefaultValue = false)]
        public string Configuration { get; set; }

        [DataMember(Name = "platform", EmitDefaultValue = false)]
        public string Platform { get; set; }

        [DataMember(Name = "solutionPath", EmitDefaultValue = false)]
        public string SolutionPath { get; set; }

        [DataMember(Name = "saveCurrentSolution", EmitDefaultValue = false)]
        public bool? SaveCurrentSolution { get; set; }
    }

    [DataContract]
    internal sealed class BridgeResponse
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "resultJson", EmitDefaultValue = false)]
        public string ResultJson { get; set; }

        [DataMember(Name = "error", EmitDefaultValue = false)]
        public string Error { get; set; }

        public static BridgeResponse FromResult(string id, object result)
        {
            return new BridgeResponse
            {
                Id = id,
                Success = true,
                ResultJson = JsonWire.SerializeObject(result)
            };
        }

        public static BridgeResponse FromError(string id, string error)
        {
            return new BridgeResponse
            {
                Id = id,
                Success = false,
                Error = error
            };
        }
    }

    [DataContract]
    internal sealed class BridgeInstanceInfo
    {
        [DataMember(Name = "protocolVersion")]
        public int ProtocolVersion { get; set; }

        [DataMember(Name = "processId")]
        public int ProcessId { get; set; }

        [DataMember(Name = "pipeName")]
        public string PipeName { get; set; }

        [DataMember(Name = "startedAtUtc")]
        public string StartedAtUtc { get; set; }

        [DataMember(Name = "solutionPath", EmitDefaultValue = false)]
        public string SolutionPath { get; set; }
    }

    [DataContract]
    internal sealed class PingResult
    {
        [DataMember(Name = "protocolVersion")]
        public int ProtocolVersion { get; set; }
    }

    [DataContract]
    internal sealed class VisualStudioState
    {
        public VisualStudioState()
        {
            Projects = new List<ProjectInfo>();
            Configurations = new List<SolutionConfigurationInfo>();
        }

        [DataMember(Name = "processId")]
        public int ProcessId { get; set; }

        [DataMember(Name = "visualStudioVersion")]
        public string VisualStudioVersion { get; set; }

        [DataMember(Name = "solutionPath", EmitDefaultValue = false)]
        public string SolutionPath { get; set; }

        [DataMember(Name = "solutionName", EmitDefaultValue = false)]
        public string SolutionName { get; set; }

        [DataMember(Name = "buildState")]
        public string BuildState { get; set; }

        [DataMember(Name = "activeConfiguration", EmitDefaultValue = false)]
        public string ActiveConfiguration { get; set; }

        [DataMember(Name = "activePlatform", EmitDefaultValue = false)]
        public string ActivePlatform { get; set; }

        [DataMember(Name = "projects")]
        public List<ProjectInfo> Projects { get; set; }

        [DataMember(Name = "configurations")]
        public List<SolutionConfigurationInfo> Configurations { get; set; }
    }

    [DataContract]
    internal sealed class ProjectInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "uniqueName", EmitDefaultValue = false)]
        public string UniqueName { get; set; }

        [DataMember(Name = "fullName", EmitDefaultValue = false)]
        public string FullName { get; set; }

        [DataMember(Name = "kind", EmitDefaultValue = false)]
        public string Kind { get; set; }

        [DataMember(Name = "isSolutionFolder", EmitDefaultValue = false)]
        public bool IsSolutionFolder { get; set; }
    }

    [DataContract]
    internal sealed class SolutionConfigurationInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "platform", EmitDefaultValue = false)]
        public string Platform { get; set; }
    }

    [DataContract]
    internal sealed class BuildResult
    {
        public BuildResult()
        {
            Errors = new List<BuildErrorInfo>();
            OutputPanes = new List<OutputPaneResult>();
            CaptureErrors = new List<string>();
        }

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "failedProjects")]
        public int FailedProjects { get; set; }

        [DataMember(Name = "configuration", EmitDefaultValue = false)]
        public string Configuration { get; set; }

        [DataMember(Name = "platform", EmitDefaultValue = false)]
        public string Platform { get; set; }

        [DataMember(Name = "scope")]
        public string Scope { get; set; }

        [DataMember(Name = "operation")]
        public string Operation { get; set; }

        [DataMember(Name = "command")]
        public string Command { get; set; }

        [DataMember(Name = "projectName", EmitDefaultValue = false)]
        public string ProjectName { get; set; }

        [DataMember(Name = "projectUniqueName", EmitDefaultValue = false)]
        public string ProjectUniqueName { get; set; }

        [DataMember(Name = "projectFullName", EmitDefaultValue = false)]
        public string ProjectFullName { get; set; }

        [DataMember(Name = "startedAtUtc")]
        public string StartedAtUtc { get; set; }

        [DataMember(Name = "finishedAtUtc")]
        public string FinishedAtUtc { get; set; }

        [DataMember(Name = "errors")]
        public List<BuildErrorInfo> Errors { get; set; }

        [DataMember(Name = "outputPanes")]
        public List<OutputPaneResult> OutputPanes { get; set; }

        [DataMember(Name = "captureErrors")]
        public List<string> CaptureErrors { get; set; }
    }

    [DataContract]
    internal sealed class BuildErrorInfo
    {
        [DataMember(Name = "level")]
        public string Level { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "project", EmitDefaultValue = false)]
        public string Project { get; set; }

        [DataMember(Name = "file", EmitDefaultValue = false)]
        public string File { get; set; }

        [DataMember(Name = "line")]
        public int Line { get; set; }

        [DataMember(Name = "column")]
        public int Column { get; set; }
    }

    [DataContract]
    internal sealed class OutputPaneResult
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "text")]
        public string Text { get; set; }
    }

    [DataContract]
    internal sealed class CancelResult
    {
        [DataMember(Name = "requested")]
        public bool Requested { get; set; }
    }

    [DataContract]
    internal sealed class OpenSolutionResult
    {
        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "closedSolutionPath", EmitDefaultValue = false)]
        public string ClosedSolutionPath { get; set; }

        [DataMember(Name = "openedSolutionPath")]
        public string OpenedSolutionPath { get; set; }

        [DataMember(Name = "solutionName")]
        public string SolutionName { get; set; }

        [DataMember(Name = "savedCurrentSolution")]
        public bool SavedCurrentSolution { get; set; }

        [DataMember(Name = "wasAlreadyOpen")]
        public bool WasAlreadyOpen { get; set; }
    }
}
