#include "Modeling/LocalFeatures.hxx"
#include "Modeling/Repair.hxx"
#include <BRepAlgoAPI_BooleanOperation.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <NCollection_List.hxx>

using namespace OcctSharp::Native;
using namespace OcctSharp::Native::LocalFeatures;

OcctSharp_Status OCCTSHARP_CALL occtsharp_boolean_topology_history(
  const OcctSharp_ShapeHandle* const* inputs, int32_t count, int32_t argument_count,
  int32_t operation, OcctSharp_FeatureResultHandle** output) {
  if (output) *output = nullptr;
  return Guard([&] {
    Require(output != nullptr, "Missing Boolean history output.");
    Require(count >= 2 && count <= 256 && argument_count > 0 && argument_count < count,
      "Boolean history requires two to 256 inputs and nonempty argument/tool groups.");
    Require(operation >= 0 && operation <= 2, "Unsupported Boolean history operation.");
    // One private graph copy preserves each original input's full-map order.
    // The caller's input geometry and topology flags remain untouched.
    InputGraph graph(inputs, count);
    NCollection_List<TopoDS_Shape> arguments, tools;
    for (int i = 0; i < count; ++i)
      (i < argument_count ? arguments : tools).Append(graph.At(i));
    std::unique_ptr<BRepAlgoAPI_BooleanOperation> algorithm;
    if (operation == 0) algorithm = std::make_unique<BRepAlgoAPI_Fuse>();
    else if (operation == 1) algorithm = std::make_unique<BRepAlgoAPI_Cut>();
    else algorithm = std::make_unique<BRepAlgoAPI_Common>();
    algorithm->SetArguments(arguments); algorithm->SetTools(tools);
    algorithm->SetNonDestructive(true); algorithm->SetRunParallel(false);
    algorithm->SetToFillHistory(true);
    Result result(11); result.Data().Info.ready = 1;
    algorithm->Build();
    if (!algorithm->IsDone() || algorithm->HasErrors())
      result.Fail("OCCT Boolean history operation did not complete.");
    else {
      result.Owner->Result = algorithm->Shape(); result.Data().Info.done = 1;
      const auto finalMap = Map(result.Owner->Result);
      // RepairSnapshot copies the result before diagnostics/persistence. Prove
      // that the actual copier preserves every full-map slot via its exact
      // ModifiedShape correspondence, never standalone BRep byte similarity.
      std::vector<TopoDS_Shape> correspondence;
      const auto diagnosticCopy = OcctSharp::Native::Repair::Copy(result.Owner->Result, &correspondence);
      const auto diagnosticMap = Map(diagnosticCopy);
      bool stable = diagnosticMap.Extent() == finalMap.Extent()
        && correspondence.size() == static_cast<size_t>(finalMap.Extent());
      for (int i = 1; stable && i <= finalMap.Extent(); ++i)
        stable = diagnosticMap(i).IsSame(correspondence[i - 1])
          && diagnosticMap(i).Orientation() == finalMap(i).Orientation();
      // Modified/Generated/Deleted are read while this exact builder is alive;
      // Unchanged uses TShape/location identity in the final output graph.
      if (stable) History(*algorithm, graph, result);
      for (auto& item : result.Owner->History)
        if (!item.Shape.IsNull() && finalMap.Contains(item.Shape))
          item.Shape = finalMap(finalMap.FindIndex(item.Shape));
      result.Owner->Message = stable ? "Boolean topology history captured from the final output."
        : "Valid Boolean geometry; diagnostic copy order could not be verified, history unavailable.";
    }
    result.Publish(output);
  });
}
