//
// ezBimOne incremental IFC: reuse previous ODA IFCFile entities when VersionGuid is unchanged.
//
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Newtonsoft.Json;
using Revit.IFC.Common.Enums;
using Revit.IFC.Common.Utility;
using Revit.IFC.Export.Toolkit;

namespace Revit.IFC.Export.Utility
{
   /// <summary>
   /// Loads the previous IFC into the current <see cref="IFCFile"/> after <c>Clear(true)</c>
   /// and skips geometry for elements whose <see cref="Element.VersionGuid"/> matches the sidecar.
   /// </summary>
   public static class IncrementalExportCache
   {
      const int SidecarVersion = 1;

      class Sidecar
      {
         public int V { get; set; } = SidecarVersion;
         public string Fingerprint { get; set; }
         public Dictionary<string, string> Elements { get; set; } = new Dictionary<string, string>();
      }

      static IFCFileModelOptions s_modelOptions;
      static Sidecar s_previous;
      static readonly Dictionary<string, IFCAnyHandle> s_byGlobalId = new Dictionary<string, IFCAnyHandle>();
      static readonly HashSet<string> s_visited = new HashSet<string>();
      static readonly Dictionary<string, string> s_recorded = new Dictionary<string, string>();
      static int s_reused;
      static int s_exported;
      static int s_deleted;

      public static bool Active { get; private set; }

      public static void SetModelOptions(IFCFileModelOptions modelOptions)
      {
         s_modelOptions = modelOptions;
      }

      public static void Reset()
      {
         Active = false;
         s_modelOptions = null;
         s_previous = null;
         s_byGlobalId.Clear();
         s_visited.Clear();
         s_recorded.Clear();
         s_reused = 0;
         s_exported = 0;
         s_deleted = 0;
      }

      public static IFCFile TryActivate(ExporterIFC exporterIFC, Document document, IFCFile currentFile)
      {
         Active = false;
         if (exporterIFC == null || document == null || currentFile == null)
            return currentFile;

         ExportOptionsCache options = ExporterCacheManager.ExportOptionsCache;
         string fullPath = options?.FullFileName;
         if (string.IsNullOrEmpty(fullPath))
            return currentFile;

         string sidecarPath = SidecarPath(fullPath);
         if (!File.Exists(sidecarPath))
            return currentFile;

         Sidecar sidecar;
         try
         {
            sidecar = JsonConvert.DeserializeObject<Sidecar>(File.ReadAllText(sidecarPath));
         }
         catch (Exception ex)
         {
            Journal(document, "ezBimOne IFC incremental: sidecar unreadable: " + ex.Message);
            return currentFile;
         }

         if (sidecar == null || sidecar.V != SidecarVersion || sidecar.Elements == null || sidecar.Elements.Count == 0)
            return currentFile;

         if (!string.Equals(sidecar.Fingerprint, Fingerprint(options), StringComparison.Ordinal))
         {
            Journal(document, "ezBimOne IFC incremental: fingerprint mismatch, full export");
            return currentFile;
         }

         string prevPath = PrevIfcPath(fullPath);
         string readPath = File.Exists(prevPath) ? prevPath : (File.Exists(fullPath) ? fullPath : null);
         if (readPath == null)
            return currentFile;

         try
         {
            IFCFileReadOptions readOptions = new IFCFileReadOptions { FileName = readPath };
            currentFile.Read(readOptions, out int numErrors, out int _);
            if (numErrors > 0)
               throw new InvalidOperationException("IFCFile.Read reported " + numErrors + " errors");

            if (!RestoreCaches(exporterIFC, document, currentFile))
               throw new InvalidOperationException("could not restore project/site/building/contexts");

            s_previous = sidecar;
            Active = true;
            Journal(document, "ezBimOne IFC incremental: loaded " + s_byGlobalId.Count + " entities from " + readPath);
            return currentFile;
         }
         catch (Exception ex)
         {
            Journal(document, "ezBimOne IFC incremental: load failed, full export: " + ex.Message);
            Active = false;
            s_previous = null;
            s_byGlobalId.Clear();
            try
            {
               currentFile.Close();
            }
            catch
            {
            }

            if (s_modelOptions == null)
               return currentFile;

            IFCFile fresh = IFCFile.Create(s_modelOptions);
            exporterIFC.SetFile(fresh);
            return fresh;
         }
      }

      public static bool TrySkipUnchanged(ExporterIFC exporterIFC, Element element, ProductWrapper productWrapper)
      {
         if (!Active || element == null || productWrapper == null)
            return false;

         string guid = GuidOf(element);
         if (string.IsNullOrEmpty(guid))
            return false;

         string version = VersionGuidOf(element);
         if (version == null)
            return false;

         if (!s_previous.Elements.TryGetValue(guid, out string previousVersion) ||
             !string.Equals(previousVersion, version, StringComparison.Ordinal))
            return false;

         if (!s_byGlobalId.TryGetValue(guid, out IFCAnyHandle handle) ||
             IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
            return false;

         ExporterCacheManager.ElementToHandleCache.Register(element.Id, handle);
         ExporterCacheManager.HandleToElementCache.Register(handle, element.Id);
         ExporterCacheManager.GUIDCache.Add(guid);
         s_reused++;
         return true;
      }

      public static void PrepareChanged(ExporterIFC exporterIFC, Element element)
      {
         if (!Active || element == null)
            return;

         string guid = GuidOf(element);
         if (string.IsNullOrEmpty(guid))
            return;

         if (!s_byGlobalId.TryGetValue(guid, out IFCAnyHandle handle) ||
             IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
            return;

         DeleteProduct(exporterIFC.GetFile(), handle, guid);
      }

      public static void Record(Element element)
      {
         if (element == null)
            return;

         string guid = GuidOf(element);
         if (string.IsNullOrEmpty(guid))
            return;

         s_visited.Add(guid);
         string version = VersionGuidOf(element);
         if (version != null)
            s_recorded[guid] = version;

         if (Active && s_previous != null &&
             s_previous.Elements.TryGetValue(guid, out string prev) &&
             string.Equals(prev, version, StringComparison.Ordinal) &&
             s_byGlobalId.ContainsKey(guid))
            return;

         s_exported++;
      }

      public static void SweepDeleted(ExporterIFC exporterIFC)
      {
         if (!Active || exporterIFC == null || s_previous == null)
            return;

         IFCFile file = exporterIFC.GetFile();
         List<string> stale = new List<string>();
         foreach (string guid in s_previous.Elements.Keys)
         {
            if (s_visited.Contains(guid))
               continue;
            if (!s_byGlobalId.TryGetValue(guid, out IFCAnyHandle handle) ||
                IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
               continue;
            if (IsProtectedSpatial(handle))
               continue;
            stale.Add(guid);
         }

         foreach (string guid in stale)
         {
            DeleteProduct(file, s_byGlobalId[guid], guid);
            s_deleted++;
         }
      }

      public static void SaveSidecar()
      {
         ExportOptionsCache options = ExporterCacheManager.ExportOptionsCache;
         string fullPath = options?.FullFileName;
         if (string.IsNullOrEmpty(fullPath) || s_recorded.Count == 0)
            return;

         try
         {
            Sidecar sidecar = new Sidecar
            {
               Fingerprint = Fingerprint(options),
               Elements = s_recorded
            };
            File.WriteAllText(SidecarPath(fullPath), JsonConvert.SerializeObject(sidecar, Formatting.Indented));
         }
         catch
         {
         }
      }

      public static void WriteJournalSummary(Document document)
      {
         if (document == null)
            return;
         Journal(document,
            "ezBimOne IFC incremental summary: active=" + Active +
            " reused=" + s_reused + " exported=" + s_exported + " deleted=" + s_deleted);
      }

      static bool RestoreCaches(ExporterIFC exporterIFC, Document document, IFCFile file)
      {
         IndexHandles(file);

         IList<IFCAnyHandle> projects = file.GetInstances(IFCEntityType.IfcProject.ToString(), false);
         if (projects == null || projects.Count == 0 || IFCAnyHandleUtil.IsNullOrHasNoValue(projects[0]))
            return false;

         IFCAnyHandle project = projects[0];
         ExporterCacheManager.ProjectHandle = project;

         IFCAnyHandle ownerHistory = IFCAnyHandleUtil.GetInstanceAttribute(project, "OwnerHistory");
         if (IFCAnyHandleUtil.IsNullOrHasNoValue(ownerHistory))
            return false;
         ExporterCacheManager.OwnerHistoryHandle = ownerHistory;
         exporterIFC.SetOwnerHistoryHandle(ownerHistory);

         IList<IFCAnyHandle> sites = file.GetInstances(IFCEntityType.IfcSite.ToString(), false);
         if (sites != null && sites.Count > 0)
            ExporterCacheManager.SiteHandle = sites[0];

         IList<IFCAnyHandle> buildings = file.GetInstances(IFCEntityType.IfcBuilding.ToString(), false);
         if (buildings != null && buildings.Count > 0)
            ExporterCacheManager.BuildingHandle = buildings[0];

         if (!RestoreContexts(exporterIFC, file))
            return false;

         RestoreLevels(exporterIFC, document);
         return true;
      }

      static void IndexHandles(IFCFile file)
      {
         s_byGlobalId.Clear();
         IndexType(file, IFCEntityType.IfcRoot, true);
         foreach (string guid in s_byGlobalId.Keys)
            ExporterCacheManager.GUIDCache.Add(guid);
      }

      static void IndexType(IFCFile file, IFCEntityType type, bool includeSubTypes)
      {
         IList<IFCAnyHandle> instances = file.GetInstances(type.ToString(), includeSubTypes);
         if (instances == null)
            return;
         foreach (IFCAnyHandle handle in instances)
         {
            string guid = ExporterUtil.GetGlobalId(handle);
            if (!string.IsNullOrEmpty(guid))
               s_byGlobalId[guid] = handle;
         }
      }

      static bool RestoreContexts(ExporterIFC exporterIFC, IFCFile file)
      {
         IList<IFCAnyHandle> contexts = file.GetInstances("IfcGeometricRepresentationContext", true);
         if (contexts == null)
            return false;

         IFCAnyHandle modelContext = null;
         foreach (IFCAnyHandle handle in contexts)
         {
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
               continue;
            string identifier = IFCAnyHandleUtil.GetStringAttribute(handle, "ContextIdentifier") ?? string.Empty;
            string contextType = IFCAnyHandleUtil.GetStringAttribute(handle, "ContextType") ?? string.Empty;
            bool isSub = IFCAnyHandleUtil.IsSubTypeOf(handle, IFCEntityType.IfcGeometricRepresentationSubContext);

            if (!isSub && string.Equals(contextType, "Model", StringComparison.OrdinalIgnoreCase))
            {
               modelContext = handle;
               ExporterCacheManager.Set3DContextHandle(exporterIFC, IFCRepresentationIdentifier.None, handle);
            }
            else if (string.Equals(identifier, "Axis", StringComparison.OrdinalIgnoreCase))
               ExporterCacheManager.Set3DContextHandle(exporterIFC, IFCRepresentationIdentifier.Axis, handle);
            else if (string.Equals(identifier, "Body", StringComparison.OrdinalIgnoreCase))
               ExporterCacheManager.Set3DContextHandle(exporterIFC, IFCRepresentationIdentifier.Body, handle);
            else if (string.Equals(identifier, "Box", StringComparison.OrdinalIgnoreCase))
               ExporterCacheManager.Set3DContextHandle(exporterIFC, IFCRepresentationIdentifier.Box, handle);
            else if (string.Equals(identifier, "FootPrint", StringComparison.OrdinalIgnoreCase))
               ExporterCacheManager.Set3DContextHandle(exporterIFC, IFCRepresentationIdentifier.FootPrint, handle);
            else if (string.Equals(identifier, "Annotation", StringComparison.OrdinalIgnoreCase))
               ExporterCacheManager.Set2DContextHandle(exporterIFC, IFCRepresentationIdentifier.Annotation, handle);
         }

         return !IFCAnyHandleUtil.IsNullOrHasNoValue(modelContext);
      }

      static void RestoreLevels(ExporterIFC exporterIFC, Document document)
      {
         double lengthScale = UnitUtil.ScaleLengthForRevitAPI();
         IList<Level> levels = LevelUtil.FindAllLevels(document);
         int count = levels?.Count ?? 0;
         for (int i = 0; i < count; i++)
         {
            Level level = levels[i];
            if (level == null)
               continue;

            string guid = GUIDUtil.GetLevelGUID(level);
            if (string.IsNullOrEmpty(guid) || !s_byGlobalId.TryGetValue(guid, out IFCAnyHandle storey) ||
                IFCAnyHandleUtil.IsNullOrHasNoValue(storey))
               continue;

            IFCAnyHandle placement = IFCAnyHandleUtil.GetObjectPlacement(storey);
            double elev = level.ProjectElevation;
            double height = 0.0;
            for (int j = i + 1; j < count; j++)
            {
               Level next = levels[j];
               if (next == null || !LevelUtil.IsBuildingStory(next))
                  continue;
               if (!MathUtil.IsAlmostEqual(next.ProjectElevation, elev))
               {
                  height = next.ProjectElevation - elev;
                  break;
               }
            }

            IFCLevelInfo info = IFCLevelInfo.Create(storey, placement, height, elev, lengthScale, true);
            ExporterCacheManager.LevelInfoCache.AddLevelInfo(exporterIFC, level.Id, info, LevelUtil.IsBuildingStory(level));
         }
      }

      static void DeleteProduct(IFCFile file, IFCAnyHandle product, string guid)
      {
         if (file == null || IFCAnyHandleUtil.IsNullOrHasNoValue(product))
            return;

         DetachFromRels(file, "IfcRelContainedInSpatialStructure", "RelatedElements", product);
         DetachFromRels(file, "IfcRelDefinesByProperties", "RelatedObjects", product);
         DetachFromRels(file, "IfcRelDefinesByType", "RelatedObjects", product);
         DetachFromRels(file, "IfcRelAssociatesMaterial", "RelatedObjects", product);
         DetachFromRels(file, "IfcRelAssignsToGroup", "RelatedObjects", product);
         DetachFromRels(file, "IfcRelAggregates", "RelatedObjects", product);
         DeleteRelsWhere(file, "IfcRelVoidsElement", "RelatingBuildingElement", product);

         IFCAnyHandleUtil.Delete(product);
         s_byGlobalId.Remove(guid);
         ExporterCacheManager.GUIDCache.Remove(guid);
      }

      static void DetachFromRels(IFCFile file, string typeName, string attribute, IFCAnyHandle product)
      {
         IList<IFCAnyHandle> rels = file.GetInstances(typeName, false);
         if (rels == null)
            return;

         int productId = product.Id;
         foreach (IFCAnyHandle rel in rels)
         {
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(rel))
               continue;
            ICollection<IFCAnyHandle> related =
               IFCAnyHandleUtil.GetAggregateInstanceAttribute<List<IFCAnyHandle>>(rel, attribute);
            if (related == null || related.Count == 0)
               continue;

            List<IFCAnyHandle> kept = new List<IFCAnyHandle>();
            bool removed = false;
            foreach (IFCAnyHandle item in related)
            {
               if (!IFCAnyHandleUtil.IsNullOrHasNoValue(item) && item.Id == productId)
               {
                  removed = true;
                  continue;
               }
               kept.Add(item);
            }

            if (!removed)
               continue;
            if (kept.Count == 0)
               IFCAnyHandleUtil.Delete(rel);
            else
               IFCAnyHandleUtil.SetAttribute(rel, attribute, kept);
         }
      }

      static void DeleteRelsWhere(IFCFile file, string typeName, string attribute, IFCAnyHandle product)
      {
         IList<IFCAnyHandle> rels = file.GetInstances(typeName, false);
         if (rels == null)
            return;

         int productId = product.Id;
         foreach (IFCAnyHandle rel in rels)
         {
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(rel))
               continue;
            IFCAnyHandle other = IFCAnyHandleUtil.GetInstanceAttribute(rel, attribute);
            if (!IFCAnyHandleUtil.IsNullOrHasNoValue(other) && other.Id == productId)
               IFCAnyHandleUtil.Delete(rel);
         }
      }

      static bool IsProtectedSpatial(IFCAnyHandle handle)
      {
         return IFCAnyHandleUtil.IsSubTypeOf(handle, IFCEntityType.IfcProject) ||
                IFCAnyHandleUtil.IsSubTypeOf(handle, IFCEntityType.IfcSite) ||
                IFCAnyHandleUtil.IsSubTypeOf(handle, IFCEntityType.IfcBuilding) ||
                IFCAnyHandleUtil.IsSubTypeOf(handle, IFCEntityType.IfcBuildingStorey);
      }

      static string GuidOf(Element element)
      {
         if (element is Level level)
            return GUIDUtil.GetLevelGUID(level);
         return GUIDUtil.GetSimpleElementIFCGUID(element);
      }

      static string VersionGuidOf(Element element)
      {
         try
         {
            return element.VersionGuid.ToString();
         }
         catch
         {
            return null;
         }
      }

      static string Fingerprint(ExportOptionsCache options)
      {
         View view = options.FilterViewForExport;
         return string.Join("|",
            options.FileVersion.ToString(),
            view?.Id.ToString() ?? string.Empty,
            options.ExcludeFilter ?? string.Empty,
            options.ExportBaseQuantities.ToString(),
            options.SiteTransformation.ToString(),
            options.IFCFileFormat.ToString());
      }

      static string SidecarPath(string fullIfcPath) => fullIfcPath + ".ezcache.json";

      static string PrevIfcPath(string fullIfcPath) => fullIfcPath + ".prev";

      static void Journal(Document document, string message)
      {
         try
         {
            document.Application.WriteJournalComment(message, true);
         }
         catch
         {
         }
      }
   }
}
