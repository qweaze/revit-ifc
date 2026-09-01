//
// ezBimOne: federated IFC export — share host IfcBuilding / IfcBuildingStorey with links.
//

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Revit.IFC.Common.Enums;
using Revit.IFC.Common.Utility;
using Revit.IFC.Export.Toolkit;

namespace Revit.IFC.Export.Utility
{
   /// <summary>
   /// Maps link levels to host storeys and orients link product placements when ExportSameBuilding.
   /// </summary>
   internal static class FederatedSameBuildingHelper
   {
      internal static bool IsActive()
      {
         return ExporterStateManager.CurrentLinkId != ElementId.InvalidElementId &&
            ExporterCacheManager.ExportOptionsCache.ExportLinkedFileAs == LinkedFileExportAs.ExportSameBuilding;
      }

      internal static void MapLinkLevelsToHost(ExporterIFC exporterIFC, Document linkDocument, Transform linkTrf)
      {
         Document hostDocument = ExportOptionsCache.HostDocument;
         if (hostDocument == null)
            return;

         IList<Level> linkLevels = LevelUtil.FindAllLevels(linkDocument);
         IList<ElementId> hostStoryIds = ExporterCacheManager.LevelInfoCache.BuildingStoriesByElevation;
         int mapped = 0;
         int unmapped = 0;

         foreach (Level linkLevel in linkLevels)
         {
            if (linkLevel == null)
               continue;

            ElementId hostStoryId = FindHostBuildingStory(hostDocument, hostStoryIds, linkLevel, linkTrf);
            if (hostStoryId == ElementId.InvalidElementId)
            {
               unmapped++;
               continue;
            }

            IFCLevelInfo hostInfo = ExporterCacheManager.LevelInfoCache.GetLevelInfo(exporterIFC, hostStoryId);
            if (hostInfo == null)
            {
               unmapped++;
               continue;
            }

            bool isStory = LevelUtil.IsBuildingStory(linkLevel);
            ExporterCacheManager.LevelInfoCache.AddLevelInfo(exporterIFC, linkLevel.Id, hostInfo, isStory);
            mapped++;
         }

         string linkTitle = string.IsNullOrEmpty(linkDocument.Title) ? linkDocument.PathName : linkDocument.Title;
         linkDocument.Application.WriteJournalComment(
            "ezBimOne IFC link building: " + linkTitle + " mapped " + mapped + " levels, unmapped " + unmapped,
            true);
      }

      internal static void TrackProductHandle(IFCAnyHandle handle)
      {
         if (!IsActive() || IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
            return;
         ExporterStateManager.FederatedLinkProductHandles.Add(handle);
      }

      internal static void OrientLinkProducts(IFCFile file, Transform linkTrf)
      {
         HashSet<IFCAnyHandle> handles = ExporterStateManager.FederatedLinkProductHandles;
         if (handles == null || handles.Count == 0)
            return;

         Transform linkTotTrf = ComputeLinkTotalTransform(linkTrf);
         foreach (IFCAnyHandle handle in handles)
         {
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
               continue;

            IFCAnyHandle placement = IFCAnyHandleUtil.GetObjectPlacement(handle);
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(placement))
               continue;

            Transform totalTrf = ExporterUtil.GetTotalTransformFromLocalPlacement(placement);
            Transform newTotalTrf = linkTotTrf.Multiply(totalTrf);

            IFCAnyHandle parentPlacement =
               IFCAnyHandleUtil.GetInstanceAttribute(placement, "PlacementRelTo");
            Transform newRelTrf;
            if (!IFCAnyHandleUtil.IsNullOrHasNoValue(parentPlacement))
            {
               Transform parentTrf = ExporterUtil.GetTotalTransformFromLocalPlacement(parentPlacement);
               newRelTrf = parentTrf.Inverse.Multiply(newTotalTrf);
            }
            else
            {
               newRelTrf = newTotalTrf;
            }

            IFCAnyHandle newRelative = ExporterUtil.CreateAxis2Placement3D(
               file, newRelTrf.Origin, newRelTrf.BasisZ, newRelTrf.BasisX);
            GeometryUtil.SetRelativePlacement(placement, newRelative);
         }
      }

      private static ElementId FindHostBuildingStory(
         Document hostDocument, IList<ElementId> hostStoryIds, Level linkLevel, Transform linkTrf)
      {
         double linkWorldZ = linkTrf.OfPoint(new XYZ(0.0, 0.0, linkLevel.ProjectElevation)).Z;

         ElementId bestId = ElementId.InvalidElementId;
         double bestDist = double.MaxValue;

         foreach (ElementId hostId in hostStoryIds)
         {
            Level hostLevel = hostDocument.GetElement(hostId) as Level;
            if (hostLevel == null || !LevelUtil.IsBuildingStory(hostLevel))
               continue;

            double dist = Math.Abs(hostLevel.ProjectElevation - linkWorldZ);
            if (dist < bestDist - 1e-9)
            {
               bestDist = dist;
               bestId = hostId;
            }
            else if (MathUtil.IsAlmostEqual(dist, bestDist) &&
               hostLevel.Name.Equals(linkLevel.Name, StringComparison.OrdinalIgnoreCase))
            {
               bestId = hostId;
            }
         }

         if (bestId != ElementId.InvalidElementId && bestDist < 0.1)
            return bestId;

         foreach (ElementId hostId in hostStoryIds)
         {
            Level hostLevel = hostDocument.GetElement(hostId) as Level;
            if (hostLevel != null &&
               hostLevel.Name.Equals(linkLevel.Name, StringComparison.OrdinalIgnoreCase))
               return hostId;
         }

         return ElementId.InvalidElementId;
      }

      internal static Transform ComputeLinkTotalTransform(Transform linkTrf)
      {
         // Host IfcSite/IfcBuilding already carry site placement; apply only the link instance transform.
         return new Transform(linkTrf);
      }
   }
}
