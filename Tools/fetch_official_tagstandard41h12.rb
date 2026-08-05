#!/usr/bin/env ruby

require "digest"
require "fileutils"
require "net/http"
require "uri"

REVISION = "f3fd9a7add5bfd82a886fc65240fdb8e3c9ac5a1"
BASE_URL =
  "https://raw.githubusercontent.com/AprilRobotics/apriltag-imgs/" \
  "#{REVISION}/tagStandard41h12"
EXPECTED_SHA256 = {
  0 => "ad5bc161c157634f05afcfd3cfba34334b9303609dd3afc9c000b86e031da02a",
  1 => "0d3011b7e8f187cb5dd7822d9075299a0141c170ba45a38b9aafdbf31292d9aa",
  2 => "d769e493648845f67555b3b07e508855beec0ed6266474eb0da0b14bb56db97b",
  3 => "da1764b41d3a1be7ce50a962210862efcb7bb73b0e512ba14e7fe2f18584b29b",
  4 => "226e7c8d405368a1404b65ad5bd3285eee3ef5eae6601134e49f4cb857035424",
  5 => "802037001e508d677d42d231917380ec2bf52a99c83b087487ad6b7625388848",
  6 => "b79b383dc5856321e4d0e2a5e187aeee20131f758b05c3c7c893f3e7f4bebfde",
  7 => "1d300f11e9b0bde7494291283b1674f7adae74a5e8f45ac2b5e79a8fd4d758c6",
  8 => "345e38547198f44066af62505c1a5a40bbe30734f21d5b5908d5d0838a81fe88",
  9 => "0d215a8915f048d89504595d077e94bf12af8e8f5ebf516ce28e17569cc50bf9",
  10 => "f66f754f511af6bd6fdb56105c57a751b2bfff6c7bd46a1fea0bbbe0b41d4dfd",
  11 => "9198a293c4fea506ef9a3d4049bf73aeab75a5cff0911a8116b88af0df8623f2",
  12 => "b079a93417d6dff1720d6aa89cd38b93ea7e3f4f0a2f5d4e6168350f9a8af1b8"
}.freeze

def tag_filename(id)
  format("tag41_12_%05d.png", id)
end

def verified?(path, expected_hash)
  File.file?(path) &&
    Digest::SHA256.file(path).hexdigest == expected_hash
end

verify_only = ARGV.first == "--verify"
ARGV.shift if verify_only
destination = ARGV.shift
abort <<~USAGE if destination.nil? || !ARGV.empty?
  Usage:
    ruby Tools/fetch_official_tagstandard41h12.rb OUTPUT_DIRECTORY
    ruby Tools/fetch_official_tagstandard41h12.rb --verify OUTPUT_DIRECTORY
USAGE

destination = File.expand_path(destination)
FileUtils.mkdir_p(destination) unless verify_only

EXPECTED_SHA256.each do |id, expected_hash|
  filename = tag_filename(id)
  output_path = File.join(destination, filename)

  if verified?(output_path, expected_hash)
    puts "OK #{filename}"
    next
  end

  abort "Missing or modified official Tag: #{output_path}" if verify_only

  response = Net::HTTP.get_response(URI("#{BASE_URL}/#{filename}"))
  unless response.is_a?(Net::HTTPSuccess)
    abort "Download failed for #{filename}: HTTP #{response.code}"
  end

  data = response.body.b
  actual_hash = Digest::SHA256.hexdigest(data)
  unless actual_hash == expected_hash
    abort "Checksum mismatch for #{filename}: #{actual_hash}"
  end

  temporary_path = "#{output_path}.download"
  File.binwrite(temporary_path, data)
  File.rename(temporary_path, output_path)
  puts "DOWNLOADED #{filename}"
end

puts
puts "All ID 0-12 files exactly match AprilRobotics revision #{REVISION}."
puts "The files were not decoded, re-encoded, rotated, mirrored, or transposed."
puts "Treat the top edge of each PNG file as the canonical official image top."
