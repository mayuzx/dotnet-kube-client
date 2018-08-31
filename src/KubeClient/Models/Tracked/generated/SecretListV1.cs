using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace KubeClient.Models.Tracked
{
    /// <summary>
    ///     SecretList is a list of Secret.
    /// </summary>
    [KubeListItem("Secret", "v1")]
    [KubeObject("SecretList", "v1")]
    public partial class SecretListV1 : Models.SecretListV1, ITracked
    {
        /// <summary>
        ///     Items is a list of secret objects. More info: https://kubernetes.io/docs/concepts/configuration/secret
        /// </summary>
        [JsonProperty("items", ObjectCreationHandling = ObjectCreationHandling.Reuse)]
        public override List<Models.SecretV1> Items { get; } = new List<Models.SecretV1>();

        /// <summary>
        ///     Names of model properties that have been modified.
        /// </summary>
        [JsonIgnore, YamlIgnore]
        public ISet<string> __ModifiedProperties__ { get; } = new HashSet<string>();
    }
}