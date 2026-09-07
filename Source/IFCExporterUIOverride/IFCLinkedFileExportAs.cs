//
// BIM IFC export alternate UI library.
//
using Revit.IFC.Export.Utility;

namespace BIM.IFC.Export.UI
{
   public class IFCLinkedFileExportAs
   {
      public LinkedFileExportAs ExportAs { get; set; }

      public IFCLinkedFileExportAs(LinkedFileExportAs exportAs)
      {
         ExportAs = exportAs;
      }

      public override string ToString()
      {
         switch (ExportAs)
         {
            case LinkedFileExportAs.DontExport:
               return "Do not export";
            case LinkedFileExportAs.ExportAsSeparate:
               return "Export as separate IFCs";
            case LinkedFileExportAs.ExportSameProject:
               return "Export in the same project";
            case LinkedFileExportAs.ExportSameSite:
               return "Export in the same site";
            case LinkedFileExportAs.ExportSameBuilding:
               return "Export links as the same building";
            default:
               return "Do not export";
         }
      }
   }
}
