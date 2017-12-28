﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor.Graphing;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace UnityEditor.ShaderGraph.Drawing
{
    public class PreviewManager : IDisposable
    {
        AbstractMaterialGraph m_Graph;
        List<PreviewRenderData> m_RenderDatas = new List<PreviewRenderData>();
        PreviewRenderData m_MasterRenderData;
        List<Identifier> m_Identifiers = new List<Identifier>();
        IndexSet m_DirtyPreviews = new IndexSet();
        IndexSet m_DirtyShaders = new IndexSet();
        IndexSet m_TimeDependentPreviews = new IndexSet();
        Material m_PreviewMaterial;
        MaterialPropertyBlock m_PreviewPropertyBlock;
        PreviewSceneResources m_SceneResources;
        Texture2D m_ErrorTexture;
        Shader m_UberShader;
        string m_OutputIdName;

        public PreviewRenderData masterRenderData
        {
            get { return m_MasterRenderData; }
        }

        public PreviewManager(AbstractMaterialGraph graph)
        {
            m_Graph = graph;
            m_PreviewMaterial = new Material(Shader.Find("Unlit/Color")) { hideFlags = HideFlags.HideInHierarchy };
            m_PreviewMaterial.hideFlags = HideFlags.HideAndDontSave;
            m_PreviewPropertyBlock = new MaterialPropertyBlock();
            m_ErrorTexture = new Texture2D(2, 2);
            m_ErrorTexture.SetPixel(0, 0, Color.magenta);
            m_ErrorTexture.SetPixel(0, 1, Color.black);
            m_ErrorTexture.SetPixel(1, 0, Color.black);
            m_ErrorTexture.SetPixel(1, 1, Color.magenta);
            m_ErrorTexture.filterMode = FilterMode.Point;
            m_ErrorTexture.Apply();
            m_SceneResources = new PreviewSceneResources();
            m_UberShader = ShaderUtil.CreateShaderAsset(k_EmptyShader);
            m_UberShader.hideFlags = HideFlags.HideAndDontSave;
            m_MasterRenderData = new PreviewRenderData
            {
                renderTexture = new RenderTexture(400, 400, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { hideFlags = HideFlags.HideAndDontSave }
            };

            foreach (var node in m_Graph.GetNodes<INode>())
                AddPreview(node);
        }

        public PreviewRenderData GetPreview(AbstractMaterialNode node)
        {
            return m_RenderDatas[node.tempId.index];
        }

        void AddPreview(INode node)
        {
            var shaderData = new PreviewShaderData
            {
                node = node
            };
            var renderData = new PreviewRenderData
            {
                shaderData = shaderData,
                renderTexture = new RenderTexture(200, 200, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { hideFlags = HideFlags.HideAndDontSave }
            };
            Set(m_Identifiers, node.tempId, node.tempId);
            Set(m_RenderDatas, node.tempId, renderData);
            m_DirtyShaders.Add(node.tempId.index);
            m_DirtyPreviews.Add(node.tempId.index);
            node.onModified += OnNodeModified;
            if (node.RequiresTime())
                m_TimeDependentPreviews.Add(node.tempId.index);

            var masterNode = node as IMasterNode;
            if (masterRenderData.shaderData == null && masterNode != null)
                masterRenderData.shaderData = shaderData;
        }

        void OnNodeModified(INode node, ModificationScope scope)
        {
            if (scope >= ModificationScope.Graph)
                m_DirtyShaders.Add(node.tempId.index);
            else if (scope == ModificationScope.Node)
                m_DirtyPreviews.Add(node.tempId.index);

            if (node.RequiresTime())
                m_TimeDependentPreviews.Add(node.tempId.index);
            else
                m_TimeDependentPreviews.Remove(node.tempId.index);
        }

        Stack<Identifier> m_Wavefront = new Stack<Identifier>();
        List<IEdge> m_Edges = new List<IEdge>();
        List<MaterialSlot> m_Slots = new List<MaterialSlot>();

        void PropagateNodeSet(IndexSet nodeSet, bool forward = true, IEnumerable<Identifier> initialWavefront = null)
        {
            m_Wavefront.Clear();
            if (initialWavefront != null)
            {
                foreach (var id in initialWavefront)
                    m_Wavefront.Push(id);
            }
            else
            {
                foreach (var index in nodeSet)
                    m_Wavefront.Push(m_Identifiers[index]);
            }
            while (m_Wavefront.Count > 0)
            {
                var index = m_Wavefront.Pop();
                var node = m_Graph.GetNodeFromTempId(index);
                if (node == null)
                    continue;

                // Loop through all nodes that the node feeds into.
                m_Slots.Clear();
                if (forward)
                    node.GetOutputSlots(m_Slots);
                else
                    node.GetInputSlots(m_Slots);
                foreach (var slot in m_Slots)
                {
                    m_Edges.Clear();
                    m_Graph.GetEdges(slot.slotReference, m_Edges);
                    foreach (var edge in m_Edges)
                    {
                        // We look at each node we feed into.
                        var connectedSlot = forward ? edge.inputSlot : edge.outputSlot;
                        var connectedNodeGuid = connectedSlot.nodeGuid;
                        var connectedNode = m_Graph.GetNodeFromGuid(connectedNodeGuid);

                        // If the input node is already in the set of time-dependent nodes, we don't need to process it.
                        if (nodeSet.Contains(connectedNode.tempId.index))
                            continue;

                        // Add the node to the set of time-dependent nodes, and to the wavefront such that we can process the nodes that it feeds into.
                        nodeSet.Add(connectedNode.tempId.index);
                        m_Wavefront.Push(connectedNode.tempId);
                    }
                }
            }
        }


        public void HandleGraphChanges()
        {
            foreach (var node in m_Graph.removedNodes)
                DestroyPreview(node.tempId);

            foreach (var node in m_Graph.addedNodes)
                AddPreview(node);

            foreach (var edge in m_Graph.removedEdges)
                m_DirtyShaders.Add(m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid).tempId.index);

            foreach (var edge in m_Graph.addedEdges)
                m_DirtyShaders.Add(m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid).tempId.index);
        }

        List<PreviewProperty> m_PreviewProperties = new List<PreviewProperty>();
        List<PreviewRenderData> m_RenderList2D = new List<PreviewRenderData>();
        List<PreviewRenderData> m_RenderList3D = new List<PreviewRenderData>();
        IndexSet m_PropertyNodes = new IndexSet();

        public void RenderPreviews()
        {
            UpdateShaders();

            // Union time dependent previews into dirty previews
            m_DirtyPreviews.UnionWith(m_TimeDependentPreviews);
            PropagateNodeSet(m_DirtyPreviews);

            // Find nodes we need properties from
            m_PropertyNodes.Clear();
            m_PropertyNodes.UnionWith(m_DirtyPreviews);
            PropagateNodeSet(m_PropertyNodes, false);

            // Fill MaterialPropertyBlock
            m_PreviewPropertyBlock.Clear();
            m_PreviewPropertyBlock.SetFloat(m_OutputIdName, -1);
            foreach (var index in m_PropertyNodes)
            {
                var node = m_Graph.GetNodeFromTempId(m_Identifiers[index]) as AbstractMaterialNode;
                if (node == null)
                    continue;
                node.CollectPreviewMaterialProperties(m_PreviewProperties);
                foreach (var prop in m_Graph.properties)
                    m_PreviewProperties.Add(prop.GetPreviewMaterialProperty());

                foreach (var previewProperty in m_PreviewProperties)
                    m_PreviewPropertyBlock.SetPreviewProperty(previewProperty);
                m_PreviewProperties.Clear();
            }

            foreach (var i in m_DirtyPreviews)
            {
                var renderData = m_RenderDatas[i];
                if (renderData.shaderData.shader == null)
                {
                    renderData.texture = null;
                    continue;
                }
                if (renderData.shaderData.hasError)
                {
                    renderData.texture = m_ErrorTexture;
                    continue;
                }

                if (renderData.previewMode == PreviewMode.Preview2D)
                    m_RenderList2D.Add(renderData);
                else
                    m_RenderList3D.Add(renderData);
            }

            m_RenderList3D.Sort((data1, data2) => data1.shaderData.shader.GetInstanceID().CompareTo(data2.shaderData.shader.GetInstanceID()));
            m_RenderList2D.Sort((data1, data2) => data1.shaderData.shader.GetInstanceID().CompareTo(data2.shaderData.shader.GetInstanceID()));

            var time = Time.realtimeSinceStartup;
            EditorUtility.SetCameraAnimateMaterialsTime(m_SceneResources.camera, time);

            m_SceneResources.light0.enabled = true;
            m_SceneResources.light0.intensity = 1.0f;
            m_SceneResources.light0.transform.rotation = Quaternion.Euler(50f, 50f, 0);
            m_SceneResources.light1.enabled = true;
            m_SceneResources.light1.intensity = 1.0f;
            m_SceneResources.camera.clearFlags = CameraClearFlags.Depth;

            // Render 2D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 2;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographicSize = 1;
            m_SceneResources.camera.orthographic = true;

            foreach (var renderData in m_RenderList2D)
                RenderPreview(renderData, m_SceneResources.quad, Matrix4x4.identity);

            // Render 3D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 5;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographic = false;

            foreach (var renderData in m_RenderList3D)
                RenderPreview(renderData, m_SceneResources.sphere, Matrix4x4.identity);

            var renderMasterPreview = masterRenderData.shaderData != null && m_DirtyPreviews.Contains(masterRenderData.shaderData.node.tempId.index);
            if (renderMasterPreview)
            {
                var mesh = m_Graph.previewData.serializedMesh.mesh ? m_Graph.previewData.serializedMesh.mesh :  m_SceneResources.sphere;
                var previewTransform = Matrix4x4.Rotate(m_Graph.previewData.rotation);
                previewTransform *= Matrix4x4.Scale(Vector3.one * (Vector3.one).magnitude / mesh.bounds.size.magnitude);
                previewTransform *= Matrix4x4.Translate(-mesh.bounds.center);
                RenderPreview(masterRenderData, mesh, previewTransform);
            }

            m_SceneResources.light0.enabled = false;
            m_SceneResources.light1.enabled = false;

            foreach (var renderData in m_RenderList2D)
                renderData.NotifyPreviewChanged();
            foreach (var renderData in m_RenderList3D)
                renderData.NotifyPreviewChanged();
            if (renderMasterPreview)
                masterRenderData.NotifyPreviewChanged();

            m_RenderList2D.Clear();
            m_RenderList3D.Clear();
            m_DirtyPreviews.Clear();
        }

        IndexSet m_NodesWith3DPreview = new IndexSet();

        void UpdateShaders()
        {
            if (m_DirtyShaders.Any())
            {
                m_NodesWith3DPreview.Clear();
                foreach (var node in m_Graph.GetNodes<AbstractMaterialNode>())
                {
                    if (node.previewMode == PreviewMode.Preview3D)
                        m_NodesWith3DPreview.Add(node.tempId.index);
                }
                PropagateNodeSet(m_NodesWith3DPreview);
                foreach (var renderData in m_RenderDatas)
                    renderData.previewMode = m_NodesWith3DPreview.Contains(renderData.shaderData.node.tempId.index) ? PreviewMode.Preview3D : PreviewMode.Preview2D;
                PropagateNodeSet(m_DirtyShaders);

                var masterNodes = new List<MasterNode>();
                var uberNodes = new List<INode>();
                foreach (var index in m_DirtyPreviews)
                {
                    var node = m_Graph.GetNodeFromTempId(m_Identifiers[index]);
                    if (node == null)
                        continue;
                    var masterNode = node as MasterNode;
                    if (masterNode != null)
                        masterNodes.Add(masterNode);
                    else
                        uberNodes.Add(node);
                }
                var count = Math.Min(uberNodes.Count, 1) + masterNodes.Count;

                try
                {
                    var i = 0;
                    EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    foreach (var node in masterNodes)
                    {
                        UpdateShader(node.tempId);
                        i++;
                        EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    }
                    if (uberNodes.Count > 0)
                    {
                        var results = m_Graph.GetUberPreviewShader();
                        m_OutputIdName = results.outputIdProperty.referenceName;
                        ShaderUtil.UpdateShaderAsset(m_UberShader, results.shader);
                        File.WriteAllText(Application.dataPath + "/../UberShader.shader", (results.shader ?? "null").Replace("UnityEngine.MaterialGraph", "Generated"));
                        bool uberShaderHasError = false;
                        if (MaterialGraphAsset.ShaderHasError(m_UberShader))
                        {
                            var errors = MaterialGraphAsset.GetShaderErrors(m_UberShader);
                            var message = new ShaderStringBuilder();
                            message.AppendLine(@"Preview shader for graph has {0} error{1}:", errors.Length, errors.Length != 1 ? "s" : "");
                            foreach (var error in errors)
                            {
                                INode node;
                                try
                                {
                                    node = results.sourceMap.FindNode(error.line);
                                }
                                catch (Exception e)
                                {
                                    Debug.LogException(e);
                                    continue;
                                }
                                message.AppendLine("{0} in {3} at line {1} (on {2})", error.message, error.line, error.platform, node != null ? string.Format("node {0} ({1})", node.name, node.guid) : "graph");
                                message.AppendLine(error.messageDetails);
                                message.AppendNewLine();
                            }
                            Debug.LogWarning(message.ToString());
                            ShaderUtil.ClearShaderErrors(m_UberShader);
                            ShaderUtil.UpdateShaderAsset(m_UberShader, k_EmptyShader);
                            uberShaderHasError = true;
                        }

                        foreach (var node in uberNodes)
                        {
                            var renderData = GetRenderData(node.tempId);
                            if (renderData == null)
                                continue;
                            var shaderData = renderData.shaderData;
                            shaderData.shader = m_UberShader;
                            shaderData.hasError = uberShaderHasError;
                        }
                        i++;
                        EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }

                // Union dirty shaders into dirty previews
                m_DirtyPreviews.UnionWith(m_DirtyShaders);
                m_DirtyShaders.Clear();
            }
        }

        void RenderPreview(PreviewRenderData renderData, Mesh mesh, Matrix4x4 transform)
        {
            m_PreviewPropertyBlock.SetFloat(m_OutputIdName, renderData.shaderData.node.tempId.index);
            if (m_PreviewMaterial.shader != renderData.shaderData.shader)
                m_PreviewMaterial.shader = renderData.shaderData.shader;
            m_SceneResources.camera.targetTexture = renderData.renderTexture;
            var previousRenderTexure = RenderTexture.active;
            RenderTexture.active = renderData.renderTexture;
            GL.Clear(true, true, Color.black);
            Graphics.Blit(Texture2D.whiteTexture, renderData.renderTexture, m_SceneResources.checkerboardMaterial);

            Graphics.DrawMesh(mesh, transform, m_PreviewMaterial, 1, m_SceneResources.camera, 0, m_PreviewPropertyBlock, ShadowCastingMode.Off, false, null, false);

            var previousUseSRP = Unsupported.useScriptableRenderPipeline;
            Unsupported.useScriptableRenderPipeline = renderData.shaderData.node is IMasterNode;
            m_SceneResources.camera.Render();
            Unsupported.useScriptableRenderPipeline = previousUseSRP;
            RenderTexture.active = previousRenderTexure;
            renderData.texture = renderData.renderTexture;
        }

        void UpdateShader(Identifier nodeId)
        {
            var node = m_Graph.GetNodeFromTempId(nodeId) as AbstractMaterialNode;
            if (node == null)
                return;
            var renderData = Get(m_RenderDatas, nodeId);
            if (renderData == null || renderData.shaderData == null)
                return;
            var shaderData = renderData.shaderData;

            if (!(node is IMasterNode) && (!node.hasPreview || NodeUtils.FindEffectiveShaderStage(node, true) == ShaderStage.Vertex))
            {
                shaderData.shaderString = null;
            }
            else
            {
                var masterNode = node as IMasterNode;
                if (masterNode != null)
                {
                    List<PropertyCollector.TextureInfo> configuredTextures;
                    shaderData.shaderString = masterNode.GetShader(GenerationMode.Preview, node.name, out configuredTextures);
                }
                else
                    shaderData.shaderString = m_Graph.GetPreviewShader(node).shader;
            }

            File.WriteAllText(Application.dataPath + "/../GeneratedShader.shader", (shaderData.shaderString ?? "null").Replace("UnityEngine.MaterialGraph", "Generated"));

            if (string.IsNullOrEmpty(shaderData.shaderString))
            {
                if (shaderData.shader != null)
                {
                    ShaderUtil.ClearShaderErrors(shaderData.shader);
                    Object.DestroyImmediate(shaderData.shader, true);
                    shaderData.shader = null;
                }
                return;
            }

            if (shaderData.shader == null)
            {
                shaderData.shader = ShaderUtil.CreateShaderAsset(shaderData.shaderString);
                shaderData.shader.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                ShaderUtil.ClearShaderErrors(shaderData.shader);
                ShaderUtil.UpdateShaderAsset(shaderData.shader, shaderData.shaderString);
            }

            // Debug output
            var message = "RecreateShader: " + node.GetVariableNameForNode() + Environment.NewLine + shaderData.shaderString;
            if (MaterialGraphAsset.ShaderHasError(shaderData.shader))
            {
                shaderData.hasError = true;
                Debug.LogWarning(message);
                ShaderUtil.ClearShaderErrors(shaderData.shader);
                Object.DestroyImmediate(shaderData.shader, true);
                shaderData.shader = null;
            }
            else
            {
                shaderData.hasError = false;
            }
        }

        void DestroyPreview(Identifier nodeId)
        {
            var renderData = Get(m_RenderDatas, nodeId);
            if (renderData != null)
            {
                if (renderData.shaderData.shader != null)
                    Object.DestroyImmediate(renderData.shaderData.shader, true);
                if (renderData.renderTexture != null)
                    Object.DestroyImmediate(renderData.renderTexture, true);
                var node = renderData.shaderData.node;
                if (node != null)
                    node.onModified -= OnNodeModified;

                m_TimeDependentPreviews.Remove(nodeId.index);
                m_DirtyPreviews.Remove(nodeId.index);
                m_DirtyPreviews.Remove(nodeId.index);
                Set(m_RenderDatas, nodeId, null);
                Set(m_Identifiers, nodeId, default(Identifier));

                if (masterRenderData.shaderData != null && masterRenderData.shaderData.node == node)
                    masterRenderData.shaderData = m_RenderDatas.Where(x => x != null && x.shaderData.node is IMasterNode).Select(x => x.shaderData).FirstOrDefault();

                renderData.shaderData.shader = null;
                renderData.renderTexture = null;
                renderData.texture = null;
                renderData.onPreviewChanged = null;
            }
        }

        void ReleaseUnmanagedResources()
        {
            if (m_PreviewMaterial != null)
                Object.DestroyImmediate(m_PreviewMaterial, true);
            m_PreviewMaterial = null;
            if (m_SceneResources != null)
                m_SceneResources.Dispose();
            m_SceneResources = null;
            var previews = m_RenderDatas.ToList();
            foreach (var renderData in previews)
                DestroyPreview(renderData.shaderData.node.tempId);
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~PreviewManager()
        {
            throw new Exception("PreviewManager was not disposed of properly.");
        }

        const string k_EmptyShader = @"
Shader ""hidden/preview""
{
    SubShader
    {
        Tags { ""RenderType""=""Opaque"" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma    vertex    vert
            #pragma    fragment    frag

            #include    ""UnityCG.cginc""

            struct    appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }
}";

        T Get<T>(List<T> list, Identifier id)
        {
            var existingId = Get(m_Identifiers, id.index);
            if (existingId.valid && existingId.version != id.version)
                throw new Exception("Identifier version mismatch");
            return Get(list, id.index);
        }

        static T Get<T>(List<T> list, int index)
        {
            return index < list.Count ? list[index] : default(T);
        }

        void Set<T>(List<T> list, Identifier id, T value)
        {
            var existingId = Get(m_Identifiers, id.index);
            if (existingId.valid && existingId.version != id.version)
                throw new Exception("Identifier version mismatch");
            Set(list, id.index, value);
        }

        static void Set<T>(List<T> list, int index, T value)
        {
            // Make sure the list is large enough for the index
            for (var i = list.Count; i <= index; i++)
                list.Add(default(T));
            list[index] = value;
        }

        PreviewRenderData GetRenderData(Identifier id)
        {
            var value = Get(m_RenderDatas, id);
            if (value != null && value.shaderData.node.tempId.version != id.version)
                throw new Exception("Trying to access render data of a previous version of a node");
            return value;
        }
    }

    public delegate void OnPreviewChanged();

    public class PreviewShaderData
    {
        public INode node { get; set; }
        public Shader shader { get; set; }
        public string shaderString { get; set; }
        public bool hasError { get; set; }
    }

    public class PreviewRenderData
    {
        public PreviewShaderData shaderData { get; set; }
        public RenderTexture renderTexture { get; set; }
        public Texture texture { get; set; }
        public PreviewMode previewMode { get; set; }
        public OnPreviewChanged onPreviewChanged;

        public void NotifyPreviewChanged()
        {
            if (onPreviewChanged != null)
                onPreviewChanged();
        }
    }
}
