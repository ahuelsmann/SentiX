// Minimal hand-rolled ONNX protobuf writer for tests: builds tiny, valid FinBERT-shaped graphs in memory so the real
// ONNX Runtime session path is exercised end-to-end without committing binary model weights to Git.
//
// Wire format: protobuf varint (wire type 0) and length-delimited (wire type 2) fields of onnx.proto
// (ModelProto, GraphProto, NodeProto, AttributeProto, TensorProto, ValueInfoProto, TypeProto, TensorShapeProto).
#pragma once

#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace sentix::testing::onnx {

class Proto {
public:
    Proto& varint_field(std::uint32_t field, std::uint64_t value) {
        key(field, 0U);
        varint(value);
        return *this;
    }
    Proto& bytes_field(std::uint32_t field, std::string_view bytes) {
        key(field, 2U);
        varint(bytes.size());
        buffer_.append(bytes);
        return *this;
    }
    Proto& message_field(std::uint32_t field, const Proto& message) { return bytes_field(field, message.bytes()); }
    [[nodiscard]] const std::string& bytes() const noexcept { return buffer_; }

private:
    void key(std::uint32_t field, std::uint32_t wireType) { varint((static_cast<std::uint64_t>(field) << 3U) | wireType); }
    void varint(std::uint64_t value) {
        while (value >= 0x80U) {
            buffer_.push_back(static_cast<char>((value & 0x7FU) | 0x80U));
            value >>= 7U;
        }
        buffer_.push_back(static_cast<char>(value));
    }

    std::string buffer_;
};

// onnx.proto TensorProto.DataType
inline constexpr std::uint64_t kFloat = 1;
inline constexpr std::uint64_t kInt64 = 7;

/// TensorShapeProto.Dimension: dim_value (> 0) or dim_param (symbolic) when value <= 0.
struct Dim {
    std::int64_t value{0};
    std::string_view param{};
};

inline Proto value_info(std::string_view name, std::uint64_t elemType, const std::vector<Dim>& dims) {
    Proto shape;
    for (const auto& d : dims) {
        Proto dimension;
        if (d.value > 0) {
            dimension.varint_field(1, static_cast<std::uint64_t>(d.value));
        } else {
            dimension.bytes_field(2, d.param);
        }
        shape.message_field(1, dimension);
    }
    Proto tensorType;
    tensorType.varint_field(1, elemType).message_field(2, shape);
    Proto type;
    type.message_field(1, tensorType);
    Proto info;
    info.bytes_field(1, name).message_field(2, type);
    return info;
}

inline Proto int_attribute(std::string_view name, std::int64_t value) {
    Proto attribute;
    attribute.bytes_field(1, name).varint_field(3, static_cast<std::uint64_t>(value)).varint_field(20, 2U /* INT */);
    return attribute;
}

inline Proto node(std::string_view opType, const std::vector<std::string>& inputs, const std::vector<std::string>& outputs,
                  const std::vector<Proto>& attributes = {}) {
    Proto n;
    for (const auto& input : inputs) {
        n.bytes_field(1, input);
    }
    for (const auto& output : outputs) {
        n.bytes_field(2, output);
    }
    n.bytes_field(3, std::string(opType) + "_" + outputs.front());
    n.bytes_field(4, opType);
    for (const auto& attribute : attributes) {
        n.message_field(5, attribute);
    }
    return n;
}

template <typename T>
inline Proto initializer(std::string_view name, std::uint64_t dataType, const std::vector<T>& values) {
    std::string raw(values.size() * sizeof(T), '\0');  // little-endian on x86_64, as raw_data requires
    std::memcpy(raw.data(), values.data(), raw.size());
    Proto tensor;
    tensor.varint_field(1, values.size()).varint_field(2, dataType).bytes_field(8, name).bytes_field(9, raw);
    return tensor;
}

struct TinyFinbertSpec {
    std::vector<float> weights{0.4F, -0.2F, 0.1F};  // logits = tokenCount * weights
    bool withTokenTypeIds{false};
    bool dynamicClassDim{false};                     // declare output as [1, "classes"] instead of [1, 3]
    std::string_view firstInputName{"input_ids"};
    std::uint64_t firstInputType{kInt64};
};

/// Graph (every declared input is consumed):
///   ids_zero = Cast<int64>(input_ids) * 0
///   counted  = attention_mask + ids_zero [+ token_type_ids]
///   logits[1, C] = ReduceSum(Cast<float>(counted), keepdims=1) * weights[C]
/// With token_type_ids == 0 this is (number of tokens incl. [CLS]/[SEP]) * weights.
inline std::string tiny_finbert_model(const TinyFinbertSpec& spec = {}) {
    const std::vector<Dim> sequence{{1, {}}, {0, "sequence"}};
    const std::int64_t classes = static_cast<std::int64_t>(spec.weights.size());
    const std::string firstInput(spec.firstInputName);

    Proto graph;
    graph.message_field(1, node("Cast", {firstInput}, {"ids_int64"}, {int_attribute("to", 7 /* INT64 */)}));
    graph.message_field(1, node("Mul", {"ids_int64", "zero_int64"}, {"ids_zero"}));
    graph.message_field(1, node("Add", {"attention_mask", "ids_zero"}, {"mask_with_ids"}));
    std::string counted = "mask_with_ids";
    if (spec.withTokenTypeIds) {
        graph.message_field(1, node("Add", {counted, "token_type_ids"}, {"mask_with_types"}));
        counted = "mask_with_types";
    }
    graph.message_field(1, node("Cast", {counted}, {"mask_float"}, {int_attribute("to", 1 /* FLOAT */)}));
    graph.message_field(1, node("ReduceSum", {"mask_float"}, {"token_count"}, {int_attribute("keepdims", 1)}));
    graph.message_field(1, node("Mul", {"token_count", "weights"}, {"logits"}));
    graph.bytes_field(2, "sentix_tiny_finbert");
    graph.message_field(5, initializer<float>("weights", kFloat, spec.weights));
    graph.message_field(5, initializer<std::int64_t>("zero_int64", kInt64, {0}));
    graph.message_field(11, value_info(spec.firstInputName, spec.firstInputType, sequence));
    graph.message_field(11, value_info("attention_mask", kInt64, sequence));
    if (spec.withTokenTypeIds) {
        graph.message_field(11, value_info("token_type_ids", kInt64, sequence));
    }
    const std::vector<Dim> logitsShape =
        spec.dynamicClassDim ? std::vector<Dim>{{1, {}}, {0, "classes"}} : std::vector<Dim>{{1, {}}, {classes, {}}};
    graph.message_field(12, value_info("logits", kFloat, logitsShape));

    Proto opset;
    opset.bytes_field(1, "").varint_field(2, 13U);
    Proto model;
    model.varint_field(1, 8U /* IR version 8 */).bytes_field(2, "sentix-tests").message_field(7, graph).message_field(8, opset);
    return model.bytes();
}

}  // namespace sentix::testing::onnx
